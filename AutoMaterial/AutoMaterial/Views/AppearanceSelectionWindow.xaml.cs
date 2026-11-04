using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutoMaterial.Models;
using AutoMaterial.Services;
using Microsoft.Win32;

namespace AutoMaterial.Views
{
    public partial class AppearanceSelectionWindow : Window
    {
        private readonly string _materialName;
        private readonly MaterialProperties _properties;
        private readonly List<TextureCandidate> _textureCandidates;
        private int _selectedCandidateIndex = 0;
        private string _customLocalImagePath = null;
        private bool _isInitializing = true;

        public MaterialAppearanceSelection UserSelection { get; private set; }

        public AppearanceSelectionWindow(
            string materialName,
            MaterialProperties properties,
            List<TextureCandidate> textureCandidates)
        {
            _isInitializing = true;
            InitializeComponent();

            _materialName = materialName ?? "Material";
            _properties = properties ?? new MaterialProperties();
            _textureCandidates = textureCandidates ?? new List<TextureCandidate>();

            lblMaterialTitle.Text = $"Escolha a Aparência para '{_materialName}'";

            SetupRgbPreview();
            PopulateTextureCards();

            _isInitializing = false;
        }

        private void SetupRgbPreview()
        {
            if (_properties?.RgbColor != null)
            {
                byte r = (byte)Math.Max(0, Math.Min(255, _properties.RgbColor.R));
                byte g = (byte)Math.Max(0, Math.Min(255, _properties.RgbColor.G));
                byte b = (byte)Math.Max(0, Math.Min(255, _properties.RgbColor.B));
                brdColorPreview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
                lblRgbInfo.Text = $"Cor determinada para o material: RGB ({r}, {g}, {b})";
            }
        }

        private void PopulateTextureCards()
        {
            if (_textureCandidates == null || _textureCandidates.Count == 0)
            {
                if (brdImg1 != null) brdImg1.Visibility = Visibility.Collapsed;
                if (brdImg2 != null) brdImg2.Visibility = Visibility.Collapsed;
                if (brdImg3 != null) brdImg3.Visibility = Visibility.Collapsed;
                if (rbInternet != null) rbInternet.IsEnabled = false;
                if (rbRgbOnly != null) rbRgbOnly.IsChecked = true;
                return;
            }

            // Ativa o rádio da Opção 1 por padrão
            if (rbInternet != null) rbInternet.IsChecked = true;

            // Card 1
            if (_textureCandidates.Count > 0)
            {
                BindCard(_textureCandidates[0], imgCard1, lblTitle1, rbCard1);
            }
            else if (brdImg1 != null)
            {
                brdImg1.Visibility = Visibility.Collapsed;
            }

            // Card 2
            if (_textureCandidates.Count > 1)
            {
                BindCard(_textureCandidates[1], imgCard2, lblTitle2, rbCard2);
            }
            else if (brdImg2 != null)
            {
                brdImg2.Visibility = Visibility.Collapsed;
            }

            // Card 3
            if (_textureCandidates.Count > 2)
            {
                BindCard(_textureCandidates[2], imgCard3, lblTitle3, rbCard3);
            }
            else if (brdImg3 != null)
            {
                brdImg3.Visibility = Visibility.Collapsed;
            }

            _isInitializing = false;
            UpdateCardHighlight(0);
        }

        private void BindCard(TextureCandidate candidate, Image imgControl, TextBlock titleControl, RadioButton rb)
        {
            if (candidate == null || imgControl == null || titleControl == null) return;

            titleControl.Text = $"{candidate.Title}\n({candidate.SourceName})";
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(candidate.ThumbnailUrl, UriKind.RelativeOrAbsolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.EndInit();
                imgControl.Source = bitmap;
            }
            catch
            {
                titleControl.Text = candidate.Title;
            }
        }

        private void UpdateCardHighlight(int index)
        {
            if (brdImg1 == null || brdImg2 == null || brdImg3 == null) return;
            if (rbCard1 == null || rbCard2 == null || rbCard3 == null) return;

            _selectedCandidateIndex = index;

            if (_textureCandidates != null && _textureCandidates.Count > 0)
            {
                brdImg1.BorderBrush = (index == 0) ? new SolidColorBrush(Color.FromRgb(37, 99, 235)) : new SolidColorBrush(Color.FromRgb(203, 213, 225));
                brdImg1.BorderThickness = (index == 0) ? new Thickness(2) : new Thickness(1);
                brdImg1.Background = (index == 0) ? new SolidColorBrush(Color.FromRgb(241, 245, 249)) : new SolidColorBrush(Color.FromRgb(248, 250, 252));
                rbCard1.IsChecked = (index == 0);
            }

            if (_textureCandidates != null && _textureCandidates.Count > 1)
            {
                brdImg2.BorderBrush = (index == 1) ? new SolidColorBrush(Color.FromRgb(37, 99, 235)) : new SolidColorBrush(Color.FromRgb(203, 213, 225));
                brdImg2.BorderThickness = (index == 1) ? new Thickness(2) : new Thickness(1);
                brdImg2.Background = (index == 1) ? new SolidColorBrush(Color.FromRgb(241, 245, 249)) : new SolidColorBrush(Color.FromRgb(248, 250, 252));
                rbCard2.IsChecked = (index == 1);
            }

            if (_textureCandidates != null && _textureCandidates.Count > 2)
            {
                brdImg3.BorderBrush = (index == 2) ? new SolidColorBrush(Color.FromRgb(37, 99, 235)) : new SolidColorBrush(Color.FromRgb(203, 213, 225));
                brdImg3.BorderThickness = (index == 2) ? new Thickness(2) : new Thickness(1);
                brdImg3.Background = (index == 2) ? new SolidColorBrush(Color.FromRgb(241, 245, 249)) : new SolidColorBrush(Color.FromRgb(248, 250, 252));
                rbCard3.IsChecked = (index == 2);
            }
        }

        private void BrdImg1_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (rbInternet != null) rbInternet.IsChecked = true;
            UpdateCardHighlight(0);
        }

        private void BrdImg2_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (rbInternet != null) rbInternet.IsChecked = true;
            UpdateCardHighlight(1);
        }

        private void BrdImg3_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (rbInternet != null) rbInternet.IsChecked = true;
            UpdateCardHighlight(2);
        }

        private void RbCard1_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            if (rbInternet != null) rbInternet.IsChecked = true;
            UpdateCardHighlight(0);
        }

        private void RbCard2_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            if (rbInternet != null) rbInternet.IsChecked = true;
            UpdateCardHighlight(1);
        }

        private void RbCard3_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            if (rbInternet != null) rbInternet.IsChecked = true;
            UpdateCardHighlight(2);
        }

        private void RbOption_Checked(object sender, RoutedEventArgs e)
        {
            // Visual responsiveness
        }

        private void BtnBrowseCustomFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Selecione uma imagem de textura para o material",
                Filter = "Imagens de Textura (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|Todos os arquivos (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                _customLocalImagePath = dlg.FileName;
                txtCustomFilePath.Text = dlg.FileName;
                rbCustomFile.IsChecked = true;
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            UserSelection = new MaterialAppearanceSelection();

            if (rbInternet.IsChecked == true)
            {
                UserSelection.Mode = AppearanceMode.InternetTexture;
                if (_textureCandidates != null && _selectedCandidateIndex < _textureCandidates.Count)
                {
                    var cand = _textureCandidates[_selectedCandidateIndex];
                    UserSelection.SelectedImagePath = cand.FullImageUrl ?? cand.ThumbnailUrl;
                    UserSelection.SelectedImageTitle = cand.Title;
                }
            }
            else if (rbCustomFile.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(_customLocalImagePath) || !File.Exists(_customLocalImagePath))
                {
                    MessageBox.Show(
                        this,
                        "Por favor, selecione um arquivo de imagem válido (.jpg ou .png) no botão 'Procurar Imagem...'.",
                        "Arquivo de Imagem Necessário",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                UserSelection.Mode = AppearanceMode.CustomFile;
                UserSelection.SelectedImagePath = _customLocalImagePath;
                UserSelection.SelectedImageTitle = Path.GetFileName(_customLocalImagePath);
            }
            else
            {
                UserSelection.Mode = AppearanceMode.RgbOnly;
                UserSelection.SelectedImagePath = null;
                UserSelection.SelectedImageTitle = "Apenas Cor Sólida RGB";
            }

            DialogResult = true;
            Close();
        }
    }
}
