using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
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

        private CancellationTokenSource _cts;

        private string _activeTemplateName = "Nenhum arquivo ativo";
        private string _outputDirectory = string.Empty;
        private string _inputFilePath = string.Empty;
        private string _apiKey = string.Empty;
        private bool _isApiKeyLocked = false;
        private string _userInstructions = string.Empty;
        private string _skuPrefix = string.Empty;

        private bool _isSelectingDimension = false;
        private DimensionMapping _pendingDimension = null;
        private CancellationTokenSource _selectionPollCts;

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
            _aiManager = new AiExtractionManager();
            _batchEngine = new LibraryBatchEngine(_swService);
            _settingsService = new UserSettingsService();
            _settingsData = _settingsService.Load();

            Mappings = new ObservableCollection<DimensionMapping>();
            Rows = new ObservableCollection<ParametricRow>();
            Logs = new ObservableCollection<ProcessingLog>();

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

            // Comandos
            AtualizarModeloAtivoCommand = new RelayCommand(AtualizarModeloAtivo);
            IniciarSelecaoCotaCommand = new RelayCommand(IniciarSelecaoCota, () => !IsProcessing && !IsSelectingDimension);
            ConfirmarCotaCommand = new RelayCommand(ConfirmarCotaSelecionada, () => IsSelectingDimension && HasPendingDimension);
            CancelarSelecaoCotaCommand = new RelayCommand(CancelarSelecaoCota, () => IsSelectingDimension);
            CapturarCotaCommand = new RelayCommand(IniciarSelecaoCota, () => !IsProcessing);
            RemoverCotaCommand = new RelayCommand(RemoverCota, () => SelectedMapping != null && !IsProcessing);
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

        public ObservableCollection<string> AvailableProviders { get; }
        public ObservableCollection<string> AvailableModels { get; }

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
            set => SetProperty(ref _inputFilePath, value);
        }

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
            ? "(✔ Salva)"
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
                }
            }
        }

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
                    return "👉 Clique agora em uma cota de Sketch ou em uma Extrusão no SolidWorks...";

                return $"✔ Detectado: {PendingDimension.FullName} ({PendingDimension.CurrentValueMm:F2} mm)";
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

        public RelayCommand AtualizarModeloAtivoCommand { get; }
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
                // Desbloqueia para o usuário editar
                IsApiKeyLocked = false;
                AddLog(LogType.Info, "Campo da API Key liberado para edição.");
            }
            else
            {
                // Salva e bloqueia novamente
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
                    MessageBox.Show("Digite ou cole sua chave de API antes de salvar.", "Chave Vazia", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    // 5 Modelos do Gemini (Priorizando os mais comuns e Low / Flash / Lite gratuitos)
                    AvailableModels.Add("gemini-3.8-flash");
                    AvailableModels.Add("gemini-2.5-flash-lite");
                    AvailableModels.Add("gemini-2.5-flash");
                    AvailableModels.Add("gemini-1.5-flash");
                    AvailableModels.Add("gemini-1.5-flash-8b");
                    SelectedModel = AvailableModels[0];
                    break;

                case "OpenAI (GPT)":
                    // 3 Modelos do GPT (Priorizando os mais comuns e Low / Mini)
                    AvailableModels.Add("gpt-4o-mini");
                    AvailableModels.Add("o3-mini");
                    AvailableModels.Add("gpt-4o");
                    SelectedModel = AvailableModels[0];
                    break;

                case "DeepSeek":
                    // 2 Modelos do DeepSeek (Mais comuns e Low)
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
                MessageBox.Show("Insira sua chave de API para detectar os modelos da sua conta.", "Chave Necessária", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    MessageBox.Show($"Conexão com a API Gemini funcionando perfeitamente!\n\nModelo selecionado automaticamente para sua conta:\n➔ {SelectedModel}", "Conexão OK", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                MessageBox.Show("Erro ao consultar modelos da conta:\n" + ex.Message, "Erro na Consulta", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

                    if (string.IsNullOrEmpty(OutputDirectory))
                    {
                        string docDir = Path.GetDirectoryName(doc.GetPathName());
                        if (!string.IsNullOrEmpty(docDir))
                        {
                            OutputDirectory = Path.Combine(docDir, "Biblioteca_Gerada");
                        }
                    }
                }
                else
                {
                    ActiveTemplateName = "Nenhum documento de peça (.sldprt) ativo";
                }
            }
            catch (Exception ex)
            {
                ActiveTemplateName = "Erro ao detectar peça: " + ex.Message;
            }
        }

        /// <summary>
        /// Inicia o fluxo invertido: o usuário clica no botão primeiro, escolhe a cota (sketch ou extrusão) no SolidWorks,
        /// e então clica no botão verde ✔ para confirmar.
        /// </summary>
        private void IniciarSelecaoCota()
        {
            try
            {
                var doc = _swService.GetActivePartDocument();
                if (doc == null)
                {
                    MessageBox.Show("Abra uma peça (.sldprt) no SolidWorks antes de iniciar a seleção de cotas.", "AutoSolid", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Limpa a seleção anterior para que o usuário escolha a cota/extrusão a partir de agora
                _swService.ClearActiveSelection();

                PendingDimension = null;
                IsSelectingDimension = true;

                _selectionPollCts?.Cancel();
                _selectionPollCts?.Dispose();
                _selectionPollCts = new CancellationTokenSource();
                var token = _selectionPollCts.Token;

                AddLog(LogType.Info, "Modo de seleção ativo: clique em uma cota de esboço ou em uma extrusão no SolidWorks e confirme no botão verde (✔).");

                Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested && IsSelectingDimension)
                    {
                        var detected = _swService.TryGetCurrentlySelectedDimension();
                        if (detected != null)
                        {
                            UpdateUiSafe(() =>
                            {
                                if (IsSelectingDimension)
                                {
                                    if (PendingDimension == null || !string.Equals(PendingDimension.FullName, detected.FullName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        PendingDimension = detected;
                                    }
                                }
                            });
                        }

                        await Task.Delay(200);
                    }
                });
            }
            catch (Exception ex)
            {
                AddLog(LogType.Error, ex.Message);
            }
        }

        private void ConfirmarCotaSelecionada()
        {
            try
            {
                var mapping = PendingDimension ?? _swService.TryGetCurrentlySelectedDimension();
                if (mapping == null)
                {
                    MessageBox.Show("Nenhuma cota ou extrusão selecionada ainda no SolidWorks.", "AutoSolid", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var existing = Mappings.FirstOrDefault(m => string.Equals(m.FullName, mapping.FullName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    AddLog(LogType.Warning, $"A cota '{mapping.FullName}' já está na lista com o apelido '{existing.Alias}'.");
                    CancelarSelecaoCota();
                    return;
                }

                string baseAlias = mapping.Alias;
                int suffix = 1;
                while (Mappings.Any(m => string.Equals(m.Alias, mapping.Alias, StringComparison.OrdinalIgnoreCase)))
                {
                    mapping.Alias = $"{baseAlias}_{suffix++}";
                }

                Mappings.Add(mapping);
                AddLog(LogType.Success, $"Cota '{mapping.FullName}' confirmada! Apelido: '{mapping.Alias}' (Valor atual: {mapping.CurrentValueMm:F2} mm).");

                CancelarSelecaoCota();
            }
            catch (Exception ex)
            {
                AddLog(LogType.Error, ex.Message);
                MessageBox.Show(ex.Message, "AutoSolid - Confirmação de Cota", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void CancelarSelecaoCota()
        {
            _selectionPollCts?.Cancel();
            _selectionPollCts?.Dispose();
            _selectionPollCts = null;

            IsSelectingDimension = false;
            PendingDimension = null;
        }

        private void RemoverCota()
        {
            if (SelectedMapping != null)
            {
                string alias = SelectedMapping.Alias;
                Mappings.Remove(SelectedMapping);
                AddLog(LogType.Info, $"Cota '{alias}' removida do mapeamento.");
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
                MessageBox.Show($"Por favor, insira sua chave de API para o provedor {SelectedProvider}.", "Chave de API Necessária", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            PersistAndLockApiKeyIfValid();

            IsProcessing = true;
            _cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();

            // Calcula uma estimativa realista inicial com base no tamanho do arquivo
            long fileSizeBytes = new FileInfo(InputFilePath).Length;
            double estimatedTotalSeconds = Math.Max(6.0, Math.Min(25.0, 5.0 + (fileSizeBytes / (1024.0 * 250.0)) * 2.5));

            ProgressValue = 5.0;
            ProgressStatusText = $"Iniciando envio para {SelectedModel}...";
            ElapsedTimeText = "⏱ Decorrido: 00:00";
            EstimatedTimeText = $"⏳ Restante: ~{FormatTime(estimatedTotalSeconds)}";
            SpeedDetailText = "Preparando requisição...";

            AddLog(LogType.Info, $"Iniciando extração via {SelectedProvider} ({SelectedModel}) para '{Path.GetFileName(InputFilePath)}'...");

            // Task em segundo plano que atualiza a barra e o cronômetro suavemente a cada 250ms
            bool extractionRunning = true;
            string currentStageText = $"Analisando documento em {SelectedModel}...";
            double minStageProgress = 10.0;

            var timerTask = Task.Run(async () =>
            {
                while (extractionRunning)
                {
                    double elapsed = sw.Elapsed.TotalSeconds;
                    double remaining = Math.Max(1.0, estimatedTotalSeconds - elapsed);

                    // Se passar da estimativa inicial, ajusta suavemente
                    if (elapsed >= estimatedTotalSeconds - 1.5)
                    {
                        remaining = 2.0;
                    }

                    // Progresso assintótico suave até 90% enquanto aguarda a IA
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
                MessageBox.Show("Erro ao extrair dados com a IA:\n\n" + ex.Message, "Erro na Extração", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            IsProcessing = true;
            _cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();

            ProgressValue = 0.0;
            ProgressStatusText = "Iniciando motor de geração SolidWorks...";
            ElapsedTimeText = "⏱ Decorrido: 00:00";
            EstimatedTimeText = $"⏳ Estimado: ~{FormatTime(Rows.Count * 2.0)}";
            SpeedDetailText = $"0 de {Rows.Count} peças";

            AddLog(LogType.Info, $"Iniciando motor de lote para gerar {Rows.Count} arquivos em '{OutputDirectory}'...");

            var progressReporter = new Progress<BatchProgressReport>(report =>
            {
                ProgressValue = report.ExactPercentage;
                ProgressStatusText = report.StageDetail;
                ElapsedTimeText = $"⏱ Decorrido: {FormatTime(report.ElapsedSeconds)}";
                EstimatedTimeText = $"⏳ Restante: ~{FormatTime(report.EstimatedRemainingSeconds)}";
                SpeedDetailText = $"⚡ {report.AverageSecondsPerItem:F1}s/peça ({report.CurrentIndex}/{report.TotalCount})";

                if (report.LogEntry != null)
                {
                    AddLog(report.LogEntry.Type, report.LogEntry.Message);
                }
            });

            try
            {
                await _batchEngine.RunBatchAsync(Mappings, Rows, OutputDirectory, progressReporter, _cts.Token);
                sw.Stop();

                ProgressValue = 100.0;
                ProgressStatusText = $"Concluído! {Rows.Count} peças processadas com sucesso.";
                ElapsedTimeText = $"⏱ Total: {FormatTime(sw.Elapsed.TotalSeconds)}";
                EstimatedTimeText = "⏳ Restante: 00:00";
                SpeedDetailText = $"✔ Biblioteca finalizada!";

                AddLog(LogType.Success, $"Biblioteca gerada em {sw.Elapsed.TotalSeconds:F1}s!");
                MessageBox.Show($"Geração da biblioteca concluída com sucesso em {sw.Elapsed.TotalSeconds:F1} segundos!", "AutoSolid", MessageBoxButtons.OK, MessageBoxIcon.Information);

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
                MessageBox.Show("Ocorreu um erro durante a geração:\n" + ex.Message, "Erro no Lote", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        /// mantendo intactas as configurações de API Key, Provedor e Modelo.
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
            if (System.Windows.Application.Current != null && System.Windows.Application.Current.Dispatcher != null)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
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
