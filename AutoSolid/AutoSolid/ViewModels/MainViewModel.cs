using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;
using AutoSolid.Core;
using AutoSolid.Models;
using AutoSolid.Services;
using SolidWorks.Interop.sldworks;

namespace AutoSolid.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        private readonly SwAutomationService _swService;
        private readonly AiExtractionManager _aiManager;
        private readonly LibraryBatchEngine _batchEngine;
        private readonly UserSettingsService _settingsService;
        private readonly AppSettingsData _settingsData;
        private readonly Dispatcher _uiDispatcher;

        private CancellationTokenSource _cts;

        private string _activeTemplateName = "Nenhum arquivo ativo";
        private string _outputDirectory = string.Empty;
        private string _inputFilePath = string.Empty;
        private string _apiKey = string.Empty;
        private bool _isApiKeyLocked = false;
        private bool _isDarkMode = true;
        private bool _isPerfilMode = false;
        private string _userInstructions = string.Empty;
        private string _skuPrefix = string.Empty;

        private bool _isSelectingDimension = false;
        private DimensionMapping _pendingDimension = null;
        private DispatcherTimer _selectionTimer;
        private readonly HashSet<string> _previousSelectionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private string _selectedProvider = "Google Gemini";
        private string _selectedModel = "gemini-3.8-flash";
        private int _selectedTabIndex = 0;

        private bool _isProcessing = false;
        private double _progressValue = 0.0;
        private string _progressStatusText = "Pronto para iniciar";
        private string _elapsedTimeText = "⏱ Decorrido: 00:00";
        private string _estimatedTimeText = "⏳ Estimado: --:--";
        private string _speedDetailText = "Aguardando comando";

        private DimensionMapping _selectedMapping;
        private ParametricRow _selectedRow;

        public MainViewModel(SwAutomationService swService)
        {
            _swService = swService;
            _uiDispatcher = Dispatcher.CurrentDispatcher;
            _aiManager = new AiExtractionManager();
            _batchEngine = new LibraryBatchEngine(_swService);
            _settingsService = new UserSettingsService();
            _settingsData = _settingsService.Load();

            _isDarkMode = _settingsData.IsDarkMode;
            _isPerfilMode = _settingsData.IsPerfilMode;

            Mappings = new ObservableCollection<DimensionMapping>();
            Rows = new ObservableCollection<ParametricRow>();
            Logs = new ObservableCollection<ProcessingLog>();

            Mappings.CollectionChanged += (s, e) => OnPropertyChanged(nameof(MappingsCountText));
            Rows.CollectionChanged += (s, e) => OnPropertyChanged(nameof(RowsCountText));

            AvailableProviders = new ObservableCollection<string>
            {
                "Google Gemini",
                "OpenAI (GPT)",
                "DeepSeek"
            };

            if (!string.IsNullOrWhiteSpace(_settingsData.SelectedProvider) && AvailableProviders.Contains(_settingsData.SelectedProvider))
            {
                _selectedProvider = _settingsData.SelectedProvider;
            }

            AvailableModels = new ObservableCollection<string>();
            UpdateModelsForCurrentProvider();

            if (!string.IsNullOrWhiteSpace(_settingsData.SelectedModel) && AvailableModels.Contains(_settingsData.SelectedModel))
            {
                _selectedModel = _settingsData.SelectedModel;
            }

            LoadSavedApiKeyForCurrentProvider();

            // Timer na thread STA principal (WPF + SolidWorks COM) para captura instantânea sem erro de thread
            _selectionTimer = new DispatcherTimer(DispatcherPriority.Normal, _uiDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _selectionTimer.Tick += SelectionTimer_Tick;

            // Comandos
            AlternarTemaCommand = new RelayCommand(AlternarTema);
            AtualizarModeloAtivoCommand = new RelayCommand(AtualizarModeloAtivo);
            AlternarModoExportacaoCommand = new RelayCommand(AlternarModoExportacao, () => !IsProcessing);
            IniciarSelecaoCotaCommand = new RelayCommand(IniciarSelecaoCota, () => !IsProcessing);
            ConfirmarCotaCommand = new RelayCommand(ConfirmarCotaSelecionada, () => IsSelectingDimension && HasPendingDimension);
            CancelarSelecaoCotaCommand = new RelayCommand(CancelarSelecaoCota, () => IsSelectingDimension);
            CapturarCotaCommand = new RelayCommand(IniciarSelecaoCota, () => !IsProcessing);
            RemoverCotaCommand = new RelayCommand(param => RemoverCota(param as DimensionMapping), _ => !IsProcessing);
            SelecionarPastaDestinoCommand = new RelayCommand(SelecionarPastaDestino);
            AvancarParaAba2Command = new RelayCommand(() => SelectedTabIndex = 1, () => Mappings.Count > 0);

            AlternarEdicaoApiKeyCommand = new RelayCommand(AlternarEdicaoApiKey, () => !IsProcessing);
            SelecionarArquivoCommand = new RelayCommand(SelecionarArquivo, () => !IsProcessing);
            ExtrairComIaCommand = new RelayCommand(async () => await ExtrairComIaAsync(), () => !IsProcessing && File.Exists(InputFilePath) && Mappings.Count > 0);
            DetectarModelosContaCommand = new RelayCommand(async () => await DetectarModelosContaAsync(), () => !IsProcessing && !string.IsNullOrWhiteSpace(ApiKey) && SelectedProvider == "Google Gemini");

            AdicionarLinhaCommand = new RelayCommand(AdicionarLinha, () => !IsProcessing);
            RemoverLinhaCommand = new RelayCommand(RemoverLinha, () => SelectedRow != null && !IsProcessing);
            LimparTabelaCommand = new RelayCommand(() => Rows.Clear(), () => Rows.Count > 0 && !IsProcessing);

            GerarBibliotecaCommand = new RelayCommand(async () => await GerarBibliotecaAsync(), () => !IsProcessing && Rows.Count > 0 && !string.IsNullOrWhiteSpace(OutputDirectory));
            CancelarCommand = new RelayCommand(Cancelar, () => IsProcessing);

            AtualizarModeloAtivo();
        }

        #region Coleções e Propriedades Observáveis

        public ObservableCollection<DimensionMapping> Mappings { get; }
        public ObservableCollection<ParametricRow> Rows { get; }
        public ObservableCollection<ProcessingLog> Logs { get; }

        public string MappingsCountText => Mappings.Count == 1 ? "1 cota" : $"{Mappings.Count} cotas";
        public string RowsCountText => Rows.Count == 1 ? "1 peça" : $"{Rows.Count} peças";

        public ObservableCollection<string> AvailableProviders { get; }
        public ObservableCollection<string> AvailableModels { get; }

        public bool IsDarkMode
        {
            get => _isDarkMode;
            set
            {
                if (SetProperty(ref _isDarkMode, value))
                {
                    if (_settingsData != null && _settingsService != null)
                    {
                        _settingsData.IsDarkMode = _isDarkMode;
                        _settingsService.Save(_settingsData);
                    }
                    OnPropertyChanged(nameof(ThemeButtonText));
                    OnPropertyChanged(nameof(ThemeButtonTooltip));
                }
            }
        }

        public string ThemeButtonText => IsDarkMode ? "☀️ Claro" : "🌙 Escuro";

        public string ThemeButtonTooltip => IsDarkMode
            ? "Alternar para o Modo Claro do SolidWorks"
            : "Alternar para o Modo Escuro do SolidWorks";

        public bool IsPerfilMode
        {
            get => _isPerfilMode;
            set
            {
                if (SetProperty(ref _isPerfilMode, value))
                {
                    if (_settingsData != null && _settingsService != null)
                    {
                        _settingsData.IsPerfilMode = _isPerfilMode;
                        _settingsService.Save(_settingsData);
                    }
                    OnPropertyChanged(nameof(ExportExtension));
                    OnPropertyChanged(nameof(TemplateSectionHeaderText));
                    OnPropertyChanged(nameof(GerarButtonText));
                    OnPropertyChanged(nameof(ExportTypeBadgeText));
                    OnPropertyChanged(nameof(ItemTypeSingularText));
                    OnPropertyChanged(nameof(ItemTypePluralText));
                }
            }
        }

        public string ExportExtension => IsPerfilMode ? ".SLDLFP" : ".SLDPRT";
        public string TemplateSectionHeaderText => IsPerfilMode ? "1. Perfil-Template Ativo (.SLDLFP)" : "1. Peça-Template Ativa (.SLDPRT)";
        public string GerarButtonText => $"✔ Aprovar e Gerar Biblioteca ({ExportExtension})";
        public string ExportTypeBadgeText => IsPerfilMode ? "PERFIL (.SLDLFP)" : "PEÇA (.SLDPRT)";
        public string ItemTypeSingularText => IsPerfilMode ? "perfil" : "peça";
        public string ItemTypePluralText => IsPerfilMode ? "perfis" : "peças";

        public string SelectedProvider
        {
            get => _selectedProvider;
            set
            {
                if (SetProperty(ref _selectedProvider, value))
                {
                    UpdateModelsForCurrentProvider();
                    LoadSavedApiKeyForCurrentProvider();
                    _settingsData.SelectedProvider = _selectedProvider;
                    _settingsService.Save(_settingsData);
                    OnPropertyChanged(nameof(ApiKeyLabel));
                    OnPropertyChanged(nameof(IsGeminiSelected));
                }
            }
        }

        public string SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (SetProperty(ref _selectedModel, value))
                {
                    if (_settingsData != null && _settingsService != null && !string.IsNullOrWhiteSpace(_selectedModel))
                    {
                        _settingsData.SelectedModel = _selectedModel;
                        _settingsService.Save(_settingsData);
                    }
                }
            }
        }

        public bool IsGeminiSelected => SelectedProvider == "Google Gemini";

        public string ApiKeyLabel
        {
            get
            {
                switch (SelectedProvider)
                {
                    case "OpenAI (GPT)": return "OpenAI API Key (sk-...):";
                    case "DeepSeek": return "DeepSeek API Key (sk-...):";
                    default: return "Gemini API Key (AI Studio):";
                }
            }
        }

        public string ActiveTemplateName
        {
            get => _activeTemplateName;
            set => SetProperty(ref _activeTemplateName, value);
        }

        public string OutputDirectory
        {
            get => _outputDirectory;
            set => SetProperty(ref _outputDirectory, value);
        }

        public string InputFilePath
        {
            get => _inputFilePath;
            set
            {
                if (SetProperty(ref _inputFilePath, value))
                {
                    OnPropertyChanged(nameof(InputFileDisplayName));
                }
            }
        }

        public string InputFileDisplayName => string.IsNullOrWhiteSpace(_inputFilePath)
            ? "Arraste a imagem/PDF da tabela aqui ou clique em Escolher"
            : Path.GetFileName(_inputFilePath);

        public string ApiKey
        {
            get => _apiKey;
            set
            {
                if (SetProperty(ref _apiKey, value))
                {
                    if (_settingsData != null && _settingsService != null && !string.IsNullOrWhiteSpace(_selectedProvider))
                    {
                        _settingsData.ProviderApiKeys[_selectedProvider] = _apiKey?.Trim() ?? string.Empty;
                        _settingsService.Save(_settingsData);
                    }
                    OnPropertyChanged(nameof(ApiKeySavedBadge));
                }
            }
        }

        public bool IsApiKeyLocked
        {
            get => _isApiKeyLocked;
            set
            {
                if (SetProperty(ref _isApiKeyLocked, value))
                {
                    OnPropertyChanged(nameof(ApiKeyEditIcon));
                    OnPropertyChanged(nameof(ApiKeyEditTooltip));
                    OnPropertyChanged(nameof(ApiKeySavedBadge));
                }
            }
        }

        public string ApiKeyEditIcon => IsApiKeyLocked ? "✏️" : "💾";

        public string ApiKeyEditTooltip => IsApiKeyLocked
            ? "Clique para editar ou alterar a chave de API salva"
            : "Clique para salvar e bloquear a chave de API";

        public string ApiKeySavedBadge => (IsApiKeyLocked && !string.IsNullOrWhiteSpace(ApiKey))
            ? "✔ Salva"
            : string.Empty;

        public string UserInstructions
        {
            get => _userInstructions;
            set => SetProperty(ref _userInstructions, value);
        }

        public string SkuPrefix
        {
            get => _skuPrefix;
            set
            {
                if (SetProperty(ref _skuPrefix, value))
                {
                    ApplySkuPrefixToAllRows();
                }
            }
        }

        public bool IsSelectingDimension
        {
            get => _isSelectingDimension;
            set
            {
                if (SetProperty(ref _isSelectingDimension, value))
                {
                    OnPropertyChanged(nameof(SelectionPromptText));
                    OnPropertyChanged(nameof(SelectDimensionButtonText));
                }
            }
        }

        public string SelectDimensionButtonText => IsSelectingDimension
            ? "🎯 Captura Ativa — Clique para Concluir"
            : "+ Selecionar Cotas / Extrusões no Modelo 3D";

        public DimensionMapping PendingDimension
        {
            get => _pendingDimension;
            set
            {
                if (SetProperty(ref _pendingDimension, value))
                {
                    OnPropertyChanged(nameof(HasPendingDimension));
                    OnPropertyChanged(nameof(SelectionPromptText));
                }
            }
        }

        public bool HasPendingDimension => PendingDimension != null;

        public string SelectionPromptText
        {
            get
            {
                if (!IsSelectingDimension)
                    return string.Empty;

                if (PendingDimension == null)
                    return "👉 Clique nas cotas, esboços ou extrusões no SolidWorks para adicioná-las direto à tabela...";

                return $"✔ Adicionada: {PendingDimension.FullName} ({PendingDimension.CurrentValueMm:F2} mm) — Clique na próxima...";
            }
        }

        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (SetProperty(ref _selectedTabIndex, value))
                {
                    if (_selectedTabIndex != 0 && IsSelectingDimension)
                    {
                        CancelarSelecaoCota();
                    }
                }
            }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (SetProperty(ref _isProcessing, value))
                {
                    OnPropertyChanged(nameof(CanEditUi));
                }
            }
        }

        public bool CanEditUi => !IsProcessing;

        public double ProgressValue
        {
            get => _progressValue;
            set => SetProperty(ref _progressValue, value);
        }

        public string ProgressStatusText
        {
            get => _progressStatusText;
            set => SetProperty(ref _progressStatusText, value);
        }

        public string ElapsedTimeText
        {
            get => _elapsedTimeText;
            set => SetProperty(ref _elapsedTimeText, value);
        }

        public string EstimatedTimeText
        {
            get => _estimatedTimeText;
            set => SetProperty(ref _estimatedTimeText, value);
        }

        public string SpeedDetailText
        {
            get => _speedDetailText;
            set => SetProperty(ref _speedDetailText, value);
        }

        public DimensionMapping SelectedMapping
        {
            get => _selectedMapping;
            set => SetProperty(ref _selectedMapping, value);
        }

        public ParametricRow SelectedRow
        {
            get => _selectedRow;
            set => SetProperty(ref _selectedRow, value);
        }

        #endregion

        #region Comandos

        public RelayCommand AlternarTemaCommand { get; }
        public RelayCommand AtualizarModeloAtivoCommand { get; }
        public RelayCommand AlternarModoExportacaoCommand { get; }
        public RelayCommand IniciarSelecaoCotaCommand { get; }
        public RelayCommand ConfirmarCotaCommand { get; }
        public RelayCommand CancelarSelecaoCotaCommand { get; }
        public RelayCommand CapturarCotaCommand { get; }
        public RelayCommand RemoverCotaCommand { get; }
        public RelayCommand SelecionarPastaDestinoCommand { get; }
        public RelayCommand AvancarParaAba2Command { get; }

        public RelayCommand SelecionarArquivoCommand { get; }
        public RelayCommand AlternarEdicaoApiKeyCommand { get; }
        public RelayCommand ExtrairComIaCommand { get; }
        public RelayCommand DetectarModelosContaCommand { get; }
        public RelayCommand AdicionarLinhaCommand { get; }
        public RelayCommand RemoverLinhaCommand { get; }
        public RelayCommand LimparTabelaCommand { get; }

        public RelayCommand GerarBibliotecaCommand { get; }
        public RelayCommand CancelarCommand { get; }

        #endregion

        #region Métodos de Negócio

        private void AlternarTema()
        {
            IsDarkMode = !IsDarkMode;
        }

        private void LoadSavedApiKeyForCurrentProvider()
        {
            if (_settingsData != null && _settingsData.ProviderApiKeys != null &&
                _settingsData.ProviderApiKeys.TryGetValue(_selectedProvider, out string savedKey) &&
                !string.IsNullOrWhiteSpace(savedKey))
            {
                _apiKey = savedKey;
                OnPropertyChanged(nameof(ApiKey));
                IsApiKeyLocked = true;
            }
            else
            {
                _apiKey = string.Empty;
                OnPropertyChanged(nameof(ApiKey));
                IsApiKeyLocked = false;
            }
        }

        private void AlternarEdicaoApiKey()
        {
            if (IsApiKeyLocked)
            {
                IsApiKeyLocked = false;
                AddLog(LogType.Info, "Campo da API Key liberado para edição.");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(ApiKey))
                {
                    _settingsData.ProviderApiKeys[SelectedProvider] = ApiKey.Trim();
                    _settingsData.SelectedProvider = SelectedProvider;
                    _settingsData.SelectedModel = SelectedModel;
                    _settingsService.Save(_settingsData);
                    IsApiKeyLocked = true;
                    AddLog(LogType.Success, $"Chave de API para {SelectedProvider} salva e persistida com sucesso!");
                }
                else
                {
                    MessageBox.Show("Digite ou cole sua chave de API antes de salvar.", "BiblioteCad — Chave Vazia", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void PersistAndLockApiKeyIfValid()
        {
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                _settingsData.ProviderApiKeys[SelectedProvider] = ApiKey.Trim();
                _settingsData.SelectedProvider = SelectedProvider;
                _settingsData.SelectedModel = SelectedModel;
                _settingsService.Save(_settingsData);
                IsApiKeyLocked = true;
            }
        }

        private void UpdateModelsForCurrentProvider()
        {
            AvailableModels.Clear();

            switch (SelectedProvider)
            {
                case "Google Gemini":
                    AvailableModels.Add("gemini-3.8-flash");
                    AvailableModels.Add("gemini-2.5-flash-lite");
                    AvailableModels.Add("gemini-2.5-flash");
                    AvailableModels.Add("gemini-1.5-flash");
                    AvailableModels.Add("gemini-1.5-flash-8b");
                    SelectedModel = AvailableModels[0];
                    break;

                case "OpenAI (GPT)":
                    AvailableModels.Add("gpt-4o-mini");
                    AvailableModels.Add("o3-mini");
                    AvailableModels.Add("gpt-4o");
                    SelectedModel = AvailableModels[0];
                    break;

                case "DeepSeek":
                    AvailableModels.Add("deepseek-chat");
                    AvailableModels.Add("deepseek-reasoner");
                    SelectedModel = AvailableModels[0];
                    break;
            }
        }

        public async Task DetectarModelosContaAsync()
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                MessageBox.Show("Insira sua chave de API para detectar os modelos da sua conta.", "BiblioteCad — Chave Necessária", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            IsProcessing = true;
            ProgressValue = 30;
            ProgressStatusText = "Consultando modelos disponíveis na sua conta do Google AI Studio...";
            ElapsedTimeText = "⏱ Decorrido: 00:01";
            EstimatedTimeText = "⏳ Estimado: ~00:02";
            SpeedDetailText = "Verificando permissões da API Key...";
            AddLog(LogType.Info, "Consultando catálogo de modelos autorizados para sua API Key...");

            try
            {
                var modelos = await _aiManager.GeminiService.ListAvailableModelsAsync(ApiKey);
                if (modelos != null && modelos.Count > 0)
                {
                    PersistAndLockApiKeyIfValid();
                    AvailableModels.Clear();
                    foreach (var m in modelos.Take(8))
                    {
                        AvailableModels.Add(m);
                    }

                    SelectedModel = AvailableModels[0];
                    ProgressValue = 100;
                    ProgressStatusText = $"Conectado! Modelo ativo: {SelectedModel}";
                    SpeedDetailText = $"{modelos.Count} modelos liberados na conta";
                    AddLog(LogType.Success, $"Conexão validada! {modelos.Count} modelos disponíveis. Selecionado: {SelectedModel}");
                    MessageBox.Show($"Conexão com a API Gemini funcionando perfeitamente!\n\nModelo selecionado automaticamente para sua conta:\n➔ {SelectedModel}", "BiblioteCad — Conexão OK", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    AddLog(LogType.Warning, "Nenhum modelo foi retornado pela API para esta chave.");
                }
            }
            catch (Exception ex)
            {
                ProgressValue = 0;
                ProgressStatusText = "Falha ao consultar modelos.";
                AddLog(LogType.Error, "Falha ao listar modelos da conta: " + ex.Message);
                MessageBox.Show("Erro ao consultar modelos da conta:\n" + ex.Message, "BiblioteCad — Erro na Consulta", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public void AtualizarModeloAtivo()
        {
            try
            {
                var doc = _swService.GetActivePartDocument();
                if (doc != null)
                {
                    ActiveTemplateName = Path.GetFileName(doc.GetPathName());
                    if (string.IsNullOrEmpty(ActiveTemplateName))
                    {
                        ActiveTemplateName = doc.GetTitle();
                    }

                    // Verificação automática do tipo de exportação (Peça .SLDPRT ou Perfil .SLDLFP)
                    bool isPerfil = _swService.DetectIfDocumentIsProfile(doc);
                    IsPerfilMode = isPerfil;

                    if (string.IsNullOrEmpty(OutputDirectory))
                    {
                        string docDir = Path.GetDirectoryName(doc.GetPathName());
                        if (!string.IsNullOrEmpty(docDir))
                        {
                            OutputDirectory = Path.Combine(docDir, IsPerfilMode ? "Perfis_Gerados" : "Biblioteca_Gerada");
                        }
                    }

                    AddLog(LogType.Info, $"Template ativo: {ActiveTemplateName} | Tipo detectado: {(IsPerfilMode ? "Perfil (.SLDLFP)" : "Peça (.SLDPRT)")}");
                }
                else
                {
                    ActiveTemplateName = "Nenhum documento de peça ou perfil (.sldprt / .sldlfp) ativo";
                }
            }
            catch (Exception ex)
            {
                ActiveTemplateName = "Erro ao detectar peça: " + ex.Message;
            }
        }

        public void AlternarModoExportacao()
        {
            IsPerfilMode = !IsPerfilMode;
            AddLog(LogType.Info, $"Modo de exportação alterado para: {ExportTypeBadgeText}");
        }

        /// <summary>
        /// Inicia o modo contínuo de seleção na thread STA principal: o usuário clica 1 vez no botão e depois vai clicando
        /// nas cotas, esboços ou extrusões no SolidWorks para adicioná-las instantaneamente na tabela sem confirmação.
        /// </summary>
        private void IniciarSelecaoCota()
        {
            try
            {
                if (IsSelectingDimension)
                {
                    CancelarSelecaoCota();
                    return;
                }

                var doc = _swService.GetActivePartDocument();
                if (doc == null)
                {
                    MessageBox.Show("Abra uma peça ou perfil (.sldprt / .sldlfp) no SolidWorks antes de iniciar a seleção de cotas.", "BiblioteCad", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                AtualizarModeloAtivo();

                // Limpa qualquer seleção anterior ao ativar o modo
                _swService.ClearActiveSelection();
                _previousSelectionSet.Clear();

                PendingDimension = null;
                IsSelectingDimension = true;
                _selectionTimer.Start();

                AddLog(LogType.Info, "Modo de seleção contínua ativo: clique nas cotas, esboços ou extrusões no SolidWorks para adicioná-las automaticamente.");
            }
            catch (Exception ex)
            {
                AddLog(LogType.Error, ex.Message);
            }
        }

        private void SelectionTimer_Tick(object sender, EventArgs e)
        {
            if (!IsSelectingDimension)
            {
                _selectionTimer.Stop();
                return;
            }

            try
            {
                var currentSelected = _swService.TryGetCurrentlySelectedDimensions();
                if (currentSelected == null || currentSelected.Count == 0)
                {
                    _previousSelectionSet.Clear();
                    return;
                }

                var currentNames = new HashSet<string>(currentSelected.Select(d => d.FullName), StringComparer.OrdinalIgnoreCase);

                foreach (var detected in currentSelected)
                {
                    if (!_previousSelectionSet.Contains(detected.FullName))
                    {
                        AdicionarCotaCapturadaAutomaticamente(detected);
                    }
                }

                _previousSelectionSet.Clear();
                foreach (var name in currentNames)
                {
                    _previousSelectionSet.Add(name);
                }
            }
            catch (Exception ex)
            {
                AddLog(LogType.Error, "Erro ao ler seleção do SolidWorks: " + ex.Message);
            }
        }

        private void AdicionarCotaCapturadaAutomaticamente(DimensionMapping mapping)
        {
            if (mapping == null) return;

            var existing = Mappings.FirstOrDefault(m => string.Equals(m.FullName, mapping.FullName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                PendingDimension = existing;
                SelectedMapping = existing;
                return;
            }

            string baseAlias = mapping.Alias;
            int suffix = 1;
            while (Mappings.Any(m => string.Equals(m.Alias, mapping.Alias, StringComparison.OrdinalIgnoreCase)))
            {
                mapping.Alias = $"{baseAlias}_{suffix++}";
            }

            Mappings.Add(mapping);
            PendingDimension = mapping;
            SelectedMapping = mapping;
            AvancarParaAba2Command?.RaiseCanExecuteChanged();

            AddLog(LogType.Success, $"Cota '{mapping.FullName}' adicionada! Apelido: '{mapping.Alias}' (Valor atual: {mapping.CurrentValueMm:F2} mm).");
        }

        private void ConfirmarCotaSelecionada()
        {
            var mapping = PendingDimension ?? _swService.TryGetCurrentlySelectedDimension();
            if (mapping != null)
            {
                AdicionarCotaCapturadaAutomaticamente(mapping);
            }
        }

        private void CancelarSelecaoCota()
        {
            _selectionTimer?.Stop();
            _previousSelectionSet.Clear();

            IsSelectingDimension = false;
            PendingDimension = null;
        }

        private void RemoverCota(DimensionMapping item = null)
        {
            var target = item ?? SelectedMapping;
            if (target != null && Mappings.Contains(target))
            {
                string alias = target.Alias;
                string fullName = target.FullName;
                Mappings.Remove(target);

                if (PendingDimension != null && string.Equals(PendingDimension.FullName, fullName, StringComparison.OrdinalIgnoreCase))
                {
                    PendingDimension = null;
                }

                _previousSelectionSet.Add(fullName);
                try
                {
                    _swService.ClearActiveSelection();
                }
                catch { }

                AvancarParaAba2Command?.RaiseCanExecuteChanged();
                AddLog(LogType.Info, $"Cota '{alias}' ({fullName}) removida do mapeamento.");
            }
        }

        private void SelecionarPastaDestino()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Selecione a pasta onde as peças geradas serão salvas:";
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrEmpty(OutputDirectory) && Directory.Exists(OutputDirectory))
                {
                    dialog.SelectedPath = OutputDirectory;
                }

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    OutputDirectory = dialog.SelectedPath;
                    AddLog(LogType.Info, $"Pasta de destino definida: {OutputDirectory}");
                }
            }
        }

        private void SelecionarArquivo()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Selecione o catálogo técnico, tabela ou imagem";
                dialog.Filter = "Documentos e Imagens|*.pdf;*.png;*.jpg;*.jpeg;*.webp;*.csv;*.txt|Todos os Arquivos|*.*";
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    InputFilePath = dialog.FileName;
                    AddLog(LogType.Info, $"Arquivo selecionado: {Path.GetFileName(InputFilePath)}");
                }
            }
        }

        private async Task ExtrairComIaAsync()
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                MessageBox.Show($"Por favor, insira sua chave de API para o provedor {SelectedProvider}.", "BiblioteCad — Chave de API Necessária", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            PersistAndLockApiKeyIfValid();

            IsProcessing = true;
            _cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();

            long fileSizeBytes = new FileInfo(InputFilePath).Length;
            double estimatedTotalSeconds = Math.Max(6.0, Math.Min(25.0, 5.0 + (fileSizeBytes / (1024.0 * 250.0)) * 2.5));

            ProgressValue = 5.0;
            ProgressStatusText = $"Iniciando envio para {SelectedModel}...";
            ElapsedTimeText = "⏱ Decorrido: 00:00";
            EstimatedTimeText = $"⏳ Restante: ~{FormatTime(estimatedTotalSeconds)}";
            SpeedDetailText = "Preparando requisição...";

            AddLog(LogType.Info, $"Iniciando extração via {SelectedProvider} ({SelectedModel}) para '{Path.GetFileName(InputFilePath)}'...");

            bool extractionRunning = true;
            string currentStageText = $"Analisando documento em {SelectedModel}...";
            double minStageProgress = 10.0;

            var timerTask = Task.Run(async () =>
            {
                while (extractionRunning)
                {
                    double elapsed = sw.Elapsed.TotalSeconds;
                    double remaining = Math.Max(1.0, estimatedTotalSeconds - elapsed);

                    if (elapsed >= estimatedTotalSeconds - 1.5)
                    {
                        remaining = 2.0;
                    }

                    double timeRatio = 1.0 - Math.Exp(-elapsed / (estimatedTotalSeconds * 0.65));
                    double calculatedProgress = Math.Max(minStageProgress, Math.Min(91.0, 10.0 + timeRatio * 81.0));

                    UpdateUiSafe(() =>
                    {
                        if (extractionRunning)
                        {
                            ProgressValue = calculatedProgress;
                            ProgressStatusText = currentStageText;
                            ElapsedTimeText = $"⏱ Decorrido: {FormatTime(elapsed)}";
                            EstimatedTimeText = $"⏳ Restante: ~{FormatTime(remaining)}";
                            SpeedDetailText = $"Processamento IA em andamento ({calculatedProgress:F0}%)";
                        }
                    });

                    await Task.Delay(250);
                }
            });

            try
            {
                var mappingsList = Mappings.ToList();
                var extractedRows = await _aiManager.ExtractDataAsync(
                    SelectedProvider,
                    InputFilePath,
                    mappingsList,
                    ApiKey,
                    SelectedModel,
                    (stagePct, stageMsg) =>
                    {
                        minStageProgress = Math.Max(minStageProgress, stagePct);
                        currentStageText = stageMsg;
                    },
                    UserInstructions);

                extractionRunning = false;
                sw.Stop();

                Rows.Clear();
                foreach (var row in extractedRows)
                {
                    row.BaseSku = row.Sku;
                    row.ApplyPrefix(SkuPrefix);
                    Rows.Add(row);
                }

                ProgressValue = 100.0;
                ProgressStatusText = $"Concluído! {Rows.Count} variações extraídas da tabela.";
                ElapsedTimeText = $"⏱ Total: {FormatTime(sw.Elapsed.TotalSeconds)}";
                EstimatedTimeText = "⏳ Restante: 00:00";
                SpeedDetailText = $"✔ {Rows.Count} linhas prontas no grid";

                AddLog(LogType.Success, $"Sucesso em {sw.Elapsed.TotalSeconds:F1}s! {Rows.Count} variações extraídas e inseridas no grid.");
            }
            catch (Exception ex)
            {
                extractionRunning = false;
                sw.Stop();

                ProgressValue = 0.0;
                ProgressStatusText = "Erro na extração com IA.";
                EstimatedTimeText = "⏳ Interrompido";
                SpeedDetailText = "Verifique o log abaixo";

                AddLog(LogType.Error, "Falha na extração por IA: " + ex.Message);
                MessageBox.Show("Erro ao extrair dados com a IA:\n\n" + ex.Message, "BiblioteCad — Erro na Extração", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                extractionRunning = false;
                IsProcessing = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void ApplySkuPrefixToAllRows()
        {
            if (Rows == null) return;
            foreach (var row in Rows)
            {
                row.ApplyPrefix(SkuPrefix);
            }
        }

        private void AdicionarLinha()
        {
            string baseSku = $"VARIAÇÃO_{Rows.Count + 1}";
            var newRow = new ParametricRow
            {
                BaseSku = baseSku,
                Sku = baseSku,
                Status = GenerationStatus.Pendente,
                StatusMessage = "Manual"
            };
            newRow.ApplyPrefix(SkuPrefix);

            foreach (var m in Mappings)
            {
                newRow.Values[m.Alias] = m.CurrentValueMm;
            }

            Rows.Add(newRow);
            AddLog(LogType.Info, $"Nova linha '{newRow.Sku}' adicionada manualmente.");
        }

        private void RemoverLinha()
        {
            if (SelectedRow != null)
            {
                string sku = SelectedRow.Sku;
                Rows.Remove(SelectedRow);
                AddLog(LogType.Info, $"Linha '{sku}' removida da tabela.");
            }
        }

        private async Task GerarBibliotecaAsync()
        {
            int totalItens = Rows.Count;
            const int limiteTabelaGrande = 20;

            if (totalItens >= limiteTabelaGrande)
            {
                int minMinutos = Math.Max(1, (int)Math.Floor((totalItens * 2.2) / 60.0));
                int maxMinutos = Math.Max(minMinutos + 1, (int)Math.Ceiling((totalItens * 3.5) / 60.0));
                string tempoEstimadoTexto = minMinutos == maxMinutos
                    ? $"{minMinutos} minutos"
                    : $"{minMinutos} a {maxMinutos} minutos";

                string mensagemConfirmacao =
                    $"O arquivo possui {totalItens} {ItemTypePluralText} e a geração completa ({ExportExtension}) pode demorar cerca de {tempoEstimadoTexto}. Devemos prosseguir?\n\n" +
                    $"💡 Sugestão: Quando a tabela for muito extensa, recomendamos fazer a geração em partes menores (por exemplo, pedindo no campo 'Instruções' para extrair por faixas como 'Apenas M4 até M10' ou removendo linhas excedentes da tabela) para manter o SolidWorks leve e rápido.";

                var confirmResult = MessageBox.Show(
                    mensagemConfirmacao,
                    "BiblioteCad — Confirmação de Lote Grande",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);

                if (confirmResult != DialogResult.Yes)
                {
                    AddLog(LogType.Info, $"Geração de {totalItens} {ItemTypePluralText} cancelada na confirmação para divisão em partes.");
                    return;
                }
            }

            IsProcessing = true;
            _cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();

            ProgressValue = 0.0;
            ProgressStatusText = $"Iniciando motor de geração SolidWorks ({ExportExtension})...";
            ElapsedTimeText = "⏱ Decorrido: 00:00";
            EstimatedTimeText = $"⏳ Estimado: ~{FormatTime(Rows.Count * 2.5)}";
            SpeedDetailText = $"0 de {Rows.Count} {ItemTypePluralText}";

            AddLog(LogType.Info, $"Iniciando motor de lote para gerar {Rows.Count} {ItemTypePluralText} ({ExportExtension}) em '{OutputDirectory}'...");

            var progressReporter = new Progress<BatchProgressReport>(report =>
            {
                ProgressValue = report.ExactPercentage;
                ProgressStatusText = report.StageDetail;
                ElapsedTimeText = $"⏱ Decorrido: {FormatTime(report.ElapsedSeconds)}";
                EstimatedTimeText = $"⏳ Restante: ~{FormatTime(report.EstimatedRemainingSeconds)}";
                SpeedDetailText = $"⚡ {report.AverageSecondsPerItem:F1}s/{ItemTypeSingularText} ({report.CurrentIndex}/{report.TotalCount})";

                if (report.LogEntry != null)
                {
                    AddLog(report.LogEntry.Type, report.LogEntry.Message);
                }
            });

            try
            {
                await _batchEngine.RunBatchAsync(Mappings, Rows, OutputDirectory, progressReporter, _cts.Token, IsPerfilMode);
                sw.Stop();

                ProgressValue = 100.0;
                ProgressStatusText = $"Concluído! {Rows.Count} {ItemTypePluralText} processados com sucesso.";
                ElapsedTimeText = $"⏱ Total: {FormatTime(sw.Elapsed.TotalSeconds)}";
                EstimatedTimeText = "⏳ Restante: 00:00";
                SpeedDetailText = $"✔ Biblioteca finalizada!";

                AddLog(LogType.Success, $"Biblioteca de {ItemTypePluralText} ({ExportExtension}) gerada em {sw.Elapsed.TotalSeconds:F1}s!");
                MessageBox.Show($"Geração da biblioteca ({ExportExtension}) concluída com sucesso em {sw.Elapsed.TotalSeconds:F1} segundos!", "BiblioteCad", MessageBoxButtons.OK, MessageBoxIcon.Information);

                LimparPainelAposGeracao();
            }
            catch (OperationCanceledException)
            {
                ProgressStatusText = "Operação cancelada pelo usuário.";
                EstimatedTimeText = "⏳ Cancelado";
                AddLog(LogType.Warning, "Geração em lote cancelada pelo usuário.");
            }
            catch (Exception ex)
            {
                ProgressStatusText = "Erro durante o processamento.";
                EstimatedTimeText = "⏳ Interrompido";
                AddLog(LogType.Error, "Erro crítico no motor de lote: " + ex.Message);
                MessageBox.Show("Ocorreu um erro durante a geração:\n" + ex.Message, "BiblioteCad — Erro no Lote", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                IsProcessing = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>
        /// Limpa todos os dados temporários do painel após finalizar uma geração em lote,
        /// mantendo intactas as configurações de API Key, Provedor, Modelo e Tema.
        /// </summary>
        private void LimparPainelAposGeracao()
        {
            CancelarSelecaoCota();

            Rows.Clear();
            Mappings.Clear();

            InputFilePath = string.Empty;
            UserInstructions = string.Empty;
            SkuPrefix = string.Empty;
            OutputDirectory = string.Empty;

            ProgressValue = 0.0;
            ProgressStatusText = "Pronto para iniciar";
            ElapsedTimeText = "⏱ Decorrido: 00:00";
            EstimatedTimeText = "⏳ Estimado: --:--";
            SpeedDetailText = "Painel limpo para novo processo";

            SelectedTabIndex = 0;
            AtualizarModeloAtivo();

            AddLog(LogType.Info, "Painel limpo automaticamente para o próximo lote (Chave de API mantida).");
        }

        private void Cancelar()
        {
            _cts?.Cancel();
        }

        private string FormatTime(double totalSeconds)
        {
            if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds) || totalSeconds < 0)
                return "00:00";

            TimeSpan ts = TimeSpan.FromSeconds(Math.Ceiling(totalSeconds));
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Minutes:D2}:{ts.Seconds:D2}s";
        }

        private void UpdateUiSafe(Action action)
        {
            if (_uiDispatcher != null && !_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }

        public void AddLog(LogType type, string message)
        {
            UpdateUiSafe(() =>
            {
                Logs.Insert(0, new ProcessingLog { Type = type, Message = message });
            });
        }

        #endregion
    }
}
