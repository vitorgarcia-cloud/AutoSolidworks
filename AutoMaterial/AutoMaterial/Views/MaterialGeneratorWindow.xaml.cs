using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutoMaterial.Models;
using AutoMaterial.Services;
using SolidWorks.Interop.sldworks;

namespace AutoMaterial.Views
{
    public partial class MaterialGeneratorWindow : Window
    {
        private readonly ISldWorks _swApp;
        private readonly AIService _aiService;
        private readonly MaterialManager _materialManager;
        private readonly UserSettingsService _settingsService;
        private AppSettingsData _settingsData;
        private bool _isInitializing = true;
        private string _selectedCustomTexturePath = null;

        public MaterialGeneratorWindow(ISldWorks swApp)
        {
            InitializeComponent();

            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _aiService = new AIService();
            _materialManager = new MaterialManager(_swApp);
            _settingsService = new UserSettingsService();

            LoadSettings();
        }

        private void RbAppearance_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || pnlImageBrowse == null) return;
            pnlImageBrowse.IsEnabled = (rbAttachImage?.IsChecked == true);
            UpdateGenerateButtonState();
        }

        private void BtnBrowseTexture_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Selecione uma imagem para a textura do material",
                Filter = "Imagens de Textura (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|Todos os arquivos (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                _selectedCustomTexturePath = dlg.FileName;
                txtCustomTexturePath.Text = dlg.FileName;
                rbAttachImage.IsChecked = true;
                UpdateGenerateButtonState();
            }
        }

        private bool HasValidAppearanceSelection()
        {
            bool hasValidImage = (rbAttachImage?.IsChecked == true) &&
                                 !string.IsNullOrWhiteSpace(_selectedCustomTexturePath) &&
                                 File.Exists(_selectedCustomTexturePath);
            bool hasRgbSelected = (rbRgbOnly?.IsChecked == true);

            return hasValidImage || hasRgbSelected;
        }

        private void UpdateGenerateButtonState()
        {
            if (btnGenerate == null) return;
            bool isValid = HasValidAppearanceSelection();
            btnGenerate.IsEnabled = isValid;
            btnGenerate.ToolTip = isValid
                ? "Gera e aplica o material físico com a aparência definida"
                : "Selecione uma imagem anexada ou marque a opção de cor RGB para habilitar a geração";
        }

        private void LoadSettings()
        {
            _settingsData = _settingsService.Load();

            string currentProvider = _settingsData.SelectedProvider ?? "Google Gemini";
            foreach (ComboBoxItem item in cmbProvider.Items)
            {
                if (string.Equals(item.Content?.ToString(), currentProvider, StringComparison.OrdinalIgnoreCase))
                {
                    cmbProvider.SelectedItem = item;
                    break;
                }
            }

            _isInitializing = false;
            UpdateProviderUi();
            UpdateGenerateButtonState();
        }

        private void UpdateProviderUi()
        {
            string provider = (cmbProvider.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Google Gemini";

            switch (provider)
            {
                case "OpenAI (GPT)":
                    lblApiKey.Text = "OpenAI API Key (sk-...):";
                    btnBuscarModelos.Visibility = Visibility.Collapsed;
                    break;
                case "DeepSeek":
                    lblApiKey.Text = "DeepSeek API Key (sk-...):";
                    btnBuscarModelos.Visibility = Visibility.Collapsed;
                    break;
                default:
                    lblApiKey.Text = "Gemini API Key (Google AI Studio):";
                    btnBuscarModelos.Visibility = Visibility.Visible;
                    break;
            }

            if (_settingsData.ProviderApiKeys.TryGetValue(provider, out string savedKey) && !string.IsNullOrWhiteSpace(savedKey))
            {
                txtApiKey.Password = savedKey;
                if (txtApiKeyVisible != null) txtApiKeyVisible.Text = savedKey;
            }
            else
            {
                txtApiKey.Password = string.Empty;
                if (txtApiKeyVisible != null) txtApiKeyVisible.Text = string.Empty;
            }

            UpdateModelsList(provider);
        }

        private void UpdateModelsList(string provider)
        {
            cmbModel.Items.Clear();

            switch (provider)
            {
                case "Google Gemini":
                    // Modelos ultrarrápidos e estáveis no Google AI Studio (respostas em ~1 a 2s)
                    cmbModel.Items.Add("gemini-3.5-flash-lite (Recomendado - Ultrarrápido)");
                    cmbModel.Items.Add("gemini-3.5-flash");
                    cmbModel.Items.Add("gemini-3.6-flash");
                    break;

                case "OpenAI (GPT)":
                    cmbModel.Items.Add("gpt-4o-mini (Recomendado)");
                    cmbModel.Items.Add("o3-mini");
                    cmbModel.Items.Add("gpt-4o");
                    break;

                case "DeepSeek":
                    cmbModel.Items.Add("deepseek-chat");
                    cmbModel.Items.Add("deepseek-reasoner");
                    break;
            }

            string savedModel = _settingsData.SelectedModel;
            bool matched = false;
            if (!string.IsNullOrWhiteSpace(savedModel))
            {
                foreach (string item in cmbModel.Items)
                {
                    if (item.StartsWith(savedModel, StringComparison.OrdinalIgnoreCase))
                    {
                        cmbModel.SelectedItem = item;
                        matched = true;
                        break;
                    }
                }
            }

            if (!matched && cmbModel.Items.Count > 0)
            {
                cmbModel.SelectedIndex = 0;
            }
        }

        private void CmbProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            string provider = (cmbProvider.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Google Gemini";
            _settingsData.SelectedProvider = provider;
            UpdateProviderUi();
            _settingsService.Save(_settingsData);
        }

        private void TxtApiKey_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            if (txtApiKeyVisible != null && txtApiKeyVisible.Visibility != Visibility.Visible)
            {
                txtApiKeyVisible.Text = txtApiKey.Password;
            }

            string provider = (cmbProvider.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Google Gemini";
            string key = txtApiKey.Password?.Trim();
            if (!string.IsNullOrEmpty(key))
            {
                _settingsData.ProviderApiKeys[provider] = key;
                _settingsService.Save(_settingsData);
            }
        }

        private void TxtApiKeyVisible_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (txtApiKeyVisible != null && txtApiKeyVisible.Visibility == Visibility.Visible)
            {
                txtApiKey.Password = txtApiKeyVisible.Text;
            }
        }

        private void BtnToggleApiKey_Click(object sender, RoutedEventArgs e)
        {
            if (txtApiKeyVisible.Visibility == Visibility.Visible)
            {
                txtApiKey.Password = txtApiKeyVisible.Text;
                txtApiKeyVisible.Visibility = Visibility.Collapsed;
                txtApiKey.Visibility = Visibility.Visible;
                txtApiKey.Focus();
                btnToggleApiKey.Content = "👁️ Ver";
            }
            else
            {
                txtApiKeyVisible.Text = txtApiKey.Password;
                txtApiKey.Visibility = Visibility.Collapsed;
                txtApiKeyVisible.Visibility = Visibility.Visible;
                txtApiKeyVisible.Focus();
                txtApiKeyVisible.CaretIndex = txtApiKeyVisible.Text?.Length ?? 0;
                btnToggleApiKey.Content = "🔒 Ocultar";
            }
        }

        private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
        {
            if (cardAiSettings == null) return;

            bool isCurrentlyVisible = cardAiSettings.Visibility == Visibility.Visible;
            cardAiSettings.Visibility = isCurrentlyVisible ? Visibility.Collapsed : Visibility.Visible;

            if (btnToggleSettings != null)
            {
                btnToggleSettings.BorderBrush = !isCurrentlyVisible
                    ? (Brush)FindResource("AutoSolidRedBrush")
                    : (Brush)FindResource("AppleBtnSecBorderBrush");
            }
        }

        private async void BtnBuscarModelos_Click(object sender, RoutedEventArgs e)
        {
            string apiKey = txtApiKey.Password?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                MessageBox.Show(
                    this,
                    "Insira sua API Key do Google Gemini antes de buscar os modelos da conta.",
                    "AutoMaterial — Chave Necessária",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                if (txtApiKeyVisible.Visibility == Visibility.Visible)
                    txtApiKeyVisible.Focus();
                else
                    if (txtApiKeyVisible.Visibility == Visibility.Visible)
                    txtApiKeyVisible.Focus();
                else
                    txtApiKey.Focus();
                return;
            }

            SetLoadingState(true, "Consultando modelos liberados na sua conta Google AI Studio...");

            try
            {
                var modelos = await _aiService.ListAvailableGeminiModelsAsync(apiKey);
                if (modelos != null && modelos.Count > 0)
                {
                    cmbModel.Items.Clear();
                    foreach (var m in modelos)
                    {
                        cmbModel.Items.Add(m);
                    }
                    cmbModel.SelectedIndex = 0;

                    MessageBox.Show(
                        this,
                        $"Conexão validada com sucesso!\n\n{modelos.Count} modelos disponíveis na sua conta.\nModelo ativo selecionado:\n➔ {cmbModel.SelectedItem}",
                        "AutoMaterial — Conexão OK",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        this,
                        "Nenhum modelo foi retornado pela API para esta chave.",
                        "Aviso",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Erro ao consultar modelos da conta:\n\n{ex.Message}",
                    "AutoMaterial — Falha na Conexão",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetLoadingState(false);
            }
        }

        private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            string provider = (cmbProvider.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Google Gemini";
            string apiKey = txtApiKey.Password?.Trim();
            string selectedModel = cmbModel.SelectedItem?.ToString() ?? "gemini-3.5-flash-lite";
            string materialName = txtMaterialName.Text?.Trim();
            string additionalContext = txtAdditionalContext.Text?.Trim();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                if (cardAiSettings != null)
                {
                    cardAiSettings.Visibility = Visibility.Visible;
                    if (btnToggleSettings != null)
                    {
                        btnToggleSettings.BorderBrush = (Brush)FindResource("AutoSolidRedBrush");
                    }
                }

                MessageBox.Show(
                    this,
                    $"Por favor, configure sua API Key para {provider}.",
                    "Validação",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                if (txtApiKeyVisible.Visibility == Visibility.Visible)
                    txtApiKeyVisible.Focus();
                else
                    txtApiKey.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(materialName))
            {
                MessageBox.Show(
                    this,
                    "Por favor, digite a norma ou nome comercial do material.",
                    "Validação",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txtMaterialName.Focus();
                return;
            }

            // Validação estrita de aparência: exige imagem selecionada OU opção de cor RGB
            if (!HasValidAppearanceSelection())
            {
                if (rbAttachImage?.IsChecked == true && (string.IsNullOrWhiteSpace(_selectedCustomTexturePath) || !File.Exists(_selectedCustomTexturePath)))
                {
                    MessageBox.Show(
                        this,
                        "Você selecionou a opção de textura por imagem, mas ainda não anexou nenhum arquivo.\n\nPor favor, clique no botão '📁 Procurar Imagem...' para anexar uma textura (.jpg, .png) ou selecione 'Apenas Cor Sólida RGB'.",
                        "Aparência Não Definida",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    btnBrowseTexture?.Focus();
                }
                else
                {
                    MessageBox.Show(
                        this,
                        "Por favor, defina a aparência do material antes de gerar:\n\n• Anexe uma imagem de textura (clique em 'Procurar Imagem...'), OU\n• Marque a opção 'Apenas Cor Sólida RGB'.",
                        "Aparência Obrigatória",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                return;
            }

            _settingsData.SelectedProvider = provider;
            _settingsData.SelectedModel = selectedModel;
            _settingsData.ProviderApiKeys[provider] = apiKey;
            _settingsService.Save(_settingsData);

            try
            {
                SetLoadingState(true, $"Pesquisando especificações técnicas de '{materialName}'...");

                // 1. Chamada assíncrona ao serviço de IA (pesquisa técnica rigorosa e determinação física)
                MaterialProperties materialProps = await _aiService.GenerateMaterialPropertiesAsync(
                    provider,
                    apiKey,
                    selectedModel,
                    materialName,
                    additionalContext,
                    status =>
                    {
                        Dispatcher.Invoke(() => lblLoadingStatus.Text = status);
                    });

                // 2. Configura a seleção de aparência baseada na escolha do usuário diretamente na tela principal:
                var userSelection = new MaterialAppearanceSelection();

                if (rbAttachImage.IsChecked == true && !string.IsNullOrWhiteSpace(_selectedCustomTexturePath) && File.Exists(_selectedCustomTexturePath))
                {
                    lblLoadingStatus.Text = "Copiando imagem anexada para 'data\\Images\\materials-ia'...";
                    string destinationImage = TextureSearchService.CopyCustomImageToMaterialsIa(_selectedCustomTexturePath, materialName);

                    userSelection.Mode = AppearanceMode.CustomFile;
                    userSelection.SelectedImagePath = destinationImage;
                    userSelection.SelectedImageTitle = Path.GetFileName(destinationImage);
                }
                else if (rbRgbOnly.IsChecked == true)
                {
                    userSelection.Mode = AppearanceMode.RgbOnly;
                    userSelection.SelectedImagePath = null;
                    userSelection.SelectedImageTitle = "Apenas Cor Sólida RGB";
                }
                else
                {
                    // Se optou por textura mas não anexou arquivo específico, adota o mapeamento de textura padrão da família
                    userSelection.Mode = AppearanceMode.InternetTexture;
                    userSelection.SelectedImagePath = null;
                }

                // 3. Gravação do arquivo .sldmat e aplicação na peça ativa do SolidWorks
                lblLoadingStatus.Text = "Gravando biblioteca .sldmat e aplicando aparência na peça ativa...";
                string savedPath = _materialManager.CreateAndApplyMaterial(
                    materialName,
                    additionalContext,
                    materialProps,
                    userSelection);

                string appearanceModeDesc = userSelection.UseRgbOnly
                    ? "Apenas Cor Sólida RGB (sem textura)"
                    : (!string.IsNullOrWhiteSpace(userSelection.SelectedImagePath)
                        ? $"Textura personalizada ({Path.GetFileName(userSelection.SelectedImagePath)}) salva em 'materials-ia'"
                        : "Textura nativa SolidWorks");

                string textureInfo = !string.IsNullOrWhiteSpace(materialProps.Appearance?.TexturePath)
                    ? $"\n• Textura de Imagem: {Path.GetFileName(materialProps.Appearance.TexturePath)}"
                    : "";

                string shaderInfo = !string.IsNullOrWhiteSpace(materialProps.Appearance?.ShaderName)
                    ? $"\n• Shader PhotoView: {materialProps.Appearance.ShaderName}"
                    : "";

                int rColor = materialProps.RgbColor != null ? materialProps.RgbColor.R : 180;
                int gColor = materialProps.RgbColor != null ? materialProps.RgbColor.G : 182;
                int bColor = materialProps.RgbColor != null ? materialProps.RgbColor.B : 185;

                MessageBox.Show(
                    this,
                    $"Material '{materialName}' gerado e aplicado com sucesso!\n\n" +
                    $"Arquivo salvo em:\n{savedPath}\n\n" +
                    $"Configuração do SolidWorks:\n" +
                    $"• Modelo Constitutivo: {materialProps.ModelType}\n" +
                    $"• Categoria: {materialProps.ClassificationCategory}\n" +
                    $"• Fonte Pesquisada: {materialProps.PropertySource}\n" +
                    $"• Módulo Elástico (E): {(materialProps.ElasticModulus / 1e9):N2} GPa ({materialProps.ElasticModulus:E2} N/m²)\n" +
                    $"• Coef. de Poisson (ν): {materialProps.PoissonRatio:N3} | Cisalhamento (G): {(materialProps.ShearModulus / 1e9):N2} GPa\n" +
                    $"• Massa Específica (ρ): {materialProps.MassDensity:N1} kg/m³\n" +
                    $"• Escoamento / Tração / Compressão: {(materialProps.YieldStrength / 1e6):N1} / {(materialProps.TensileStrength / 1e6):N1} / {(materialProps.CompressiveStrength / 1e6):N1} MPa\n" +
                    $"• Térmico: k = {materialProps.ThermalConductivity:N2} W/(m·K) | c = {materialProps.SpecificHeat:N0} J/(kg·K)\n" +
                    $"• Escolha de Aparência: {appearanceModeDesc}\n" +
                    $"• Cor RGB: ({rColor}, {gColor}, {bColor})" +
                    textureInfo +
                    shaderInfo,
                    "Sucesso - AutoMaterial",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                SetLoadingState(false);
                MessageBox.Show(
                    this,
                    $"Ocorreu um erro ao gerar ou aplicar o material:\n\n{ex.Message}",
                    "Erro - AutoMaterial",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SetLoadingState(bool isLoading, string statusMessage = null)
        {
            pnlInputs.IsEnabled = !isLoading;
            btnGenerate.IsEnabled = !isLoading && HasValidAppearanceSelection();
            btnCancel.IsEnabled = !isLoading;

            brdLoading.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrEmpty(statusMessage))
            {
                lblLoadingStatus.Text = statusMessage;
            }
        }
    }
}
