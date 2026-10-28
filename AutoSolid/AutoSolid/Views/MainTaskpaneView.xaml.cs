using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutoSolid.Models;
using AutoSolid.ViewModels;

namespace AutoSolid.Views
{
    public partial class MainTaskpaneView : UserControl
    {
        public MainTaskpaneView()
        {
            InitializeComponent();
            LoadBrandLogoImages();
            DataContextChanged += MainTaskpaneView_DataContextChanged;
        }

        /// <summary>
        /// Carrega a logo oficial 3D do BiblioteCad diretamente do diretório da DLL
        /// para garantir renderização nítida dentro do processo nativo do SolidWorks.
        /// </summary>
        private void LoadBrandLogoImages()
        {
            try
            {
                string dllDir = Path.GetDirectoryName(typeof(MainTaskpaneView).Assembly.Location);
                if (string.IsNullOrEmpty(dllDir)) return;

                string logoPath = Path.Combine(dllDir, "Assets", "BiblioteCadLogo.png");
                if (!File.Exists(logoPath)) return;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(logoPath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                if (HeaderLogoImage != null) HeaderLogoImage.Source = bitmap;
                if (DropZoneLogoImage != null) DropZoneLogoImage.Source = bitmap;
                if (ConsoleLogoImage != null) ConsoleLogoImage.Source = bitmap;
            }
            catch
            {
                // Ignora caso o arquivo de imagem não esteja acessível
            }
        }

        private void MainTaskpaneView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is MainViewModel oldVm)
            {
                oldVm.Mappings.CollectionChanged -= Mappings_CollectionChanged;
                oldVm.PropertyChanged -= ViewModel_PropertyChanged;
                foreach (var m in oldVm.Mappings)
                {
                    UnsubscribeMapping(m);
                }
            }

            if (e.NewValue is MainViewModel newVm)
            {
                newVm.Mappings.CollectionChanged += Mappings_CollectionChanged;
                newVm.PropertyChanged += ViewModel_PropertyChanged;
                foreach (var m in newVm.Mappings)
                {
                    SubscribeMapping(m);
                }
                ApplyTheme(newVm.IsDarkMode);
                RebuildDataGridColumns(newVm);
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is MainViewModel vm)
            {
                if (e.PropertyName == nameof(MainViewModel.IsDarkMode))
                {
                    ApplyTheme(vm.IsDarkMode);
                }
                else if (e.PropertyName == nameof(MainViewModel.SelectedTabIndex))
                {
                    if (vm.SelectedTabIndex == 1)
                    {
                        MappingsDataGrid?.CommitEdit(DataGridEditingUnit.Cell, true);
                        MappingsDataGrid?.CommitEdit(DataGridEditingUnit.Row, true);
                        RebuildDataGridColumns(vm);
                    }
                }
            }
        }

        /// <summary>
        /// Aplica em tempo real a paleta de cores oficial em Vermelho SolidWorks / BiblioteCad (Modo Escuro ou Modo Claro).
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            if (isDark)
            {
                // Paleta Modo Escuro do SolidWorks + Vermelho Oficial BiblioteCad
                SetBrush("SwAppBgBrush", "#363636");
                SetBrush("SwHeaderBgBrush", "#2C2C2C");
                SetBrush("SwHeaderBorderBrush", "#505050");
                SetBrush("SwSurfaceBgBrush", "#3E3E3E");
                SetBrush("SwPanelBgBrush", "#454545");
                SetBrush("SwGroupHeaderBgBrush", "#333333");
                SetBrush("SwBorderBrush", "#585858");
                SetBrush("SwInputBgBrush", "#2A2A2A");
                SetBrush("SwInputReadOnlyBgBrush", "#343434");
                SetBrush("SwInputBorderBrush", "#626262");
                SetBrush("SwDropZoneBgBrush", "#382F2F");
                SetBrush("SwDropZoneBorderBrush", "#9E3838");

                SetBrush("SwTextDarkBrush", "#F0F0F0");
                SetBrush("SwTextMutedBrush", "#B5B8BC");
                SetBrush("SwTextHighlightBrush", "#FF5C5C");

                SetBrush("SwBlueBrush", "#C81E1E");
                SetBrush("SwBlueHoverBrush", "#E02828");
                SetBrush("SwBlueLightBgBrush", "#4A2020");
                SetBrush("SwBlueBorderBrush", "#F04444");

                SetBrush("SwGreenBrush", "#168A4A");
                SetBrush("SwGreenHoverBrush", "#1DA359");

                SetBrush("SwSecondaryBtnBgBrush", "#4E4E4E");
                SetBrush("SwSecondaryBtnBorderBrush", "#686868");

                SetBrush("SwDangerBgBrush", "#4A2727");
                SetBrush("SwDangerBorderBrush", "#8A3E3E");
                SetBrush("SwDangerTextBrush", "#FF9494");

                SetBrush("SwTabInactiveBgBrush", "#2F2F2F");
                SetBrush("SwTabInactiveTextBrush", "#A6A8AA");

                SetBrush("SwGridBgBrush", "#2A2A2A");
                SetBrush("SwGridAltRowBgBrush", "#313131");
                SetBrush("SwGridHeaderBgBrush", "#353535");
                SetBrush("SwGridLinesBrush", "#474747");
                SetBrush("SwGridSelectedBgBrush", "#6B1D1D");
                SetBrush("SwGridSelectedTextBrush", "#FFFFFF");

                SetBrush("SwBannerBgBrush", "#173D28");
                SetBrush("SwBannerBorderBrush", "#2E8B57");
                SetBrush("SwBannerTextBrush", "#85EEB1");
                SetBrush("SwBadgeBgBrush", "#4A1E1E");
            }
            else
            {
                // Paleta Modo Claro do SolidWorks + Vermelho Oficial BiblioteCad
                SetBrush("SwAppBgBrush", "#ECECEC");
                SetBrush("SwHeaderBgBrush", "#D8DCE0");
                SetBrush("SwHeaderBorderBrush", "#B4B8BC");
                SetBrush("SwSurfaceBgBrush", "#F2F3F5");
                SetBrush("SwPanelBgBrush", "#FAFAFA");
                SetBrush("SwGroupHeaderBgBrush", "#DFE3E8");
                SetBrush("SwBorderBrush", "#C2C4C6");
                SetBrush("SwInputBgBrush", "#FFFFFF");
                SetBrush("SwInputReadOnlyBgBrush", "#EAECEE");
                SetBrush("SwInputBorderBrush", "#ABADB3");
                SetBrush("SwDropZoneBgBrush", "#F7EDED");
                SetBrush("SwDropZoneBorderBrush", "#C98A8A");

                SetBrush("SwTextDarkBrush", "#1E1E1E");
                SetBrush("SwTextMutedBrush", "#55585C");
                SetBrush("SwTextHighlightBrush", "#B71C1C");

                SetBrush("SwBlueBrush", "#B71C1C");
                SetBrush("SwBlueHoverBrush", "#961515");
                SetBrush("SwBlueLightBgBrush", "#FDECEC");
                SetBrush("SwBlueBorderBrush", "#D32F2F");

                SetBrush("SwGreenBrush", "#107C41");
                SetBrush("SwGreenHoverBrush", "#0C5E31");

                SetBrush("SwSecondaryBtnBgBrush", "#F3F3F3");
                SetBrush("SwSecondaryBtnBorderBrush", "#B0B0B0");

                SetBrush("SwDangerBgBrush", "#FDF2F2");
                SetBrush("SwDangerBorderBrush", "#E0B4B4");
                SetBrush("SwDangerTextBrush", "#B91C1C");

                SetBrush("SwTabInactiveBgBrush", "#E1E3E6");
                SetBrush("SwTabInactiveTextBrush", "#505050");

                SetBrush("SwGridBgBrush", "#FFFFFF");
                SetBrush("SwGridAltRowBgBrush", "#F7F8FA");
                SetBrush("SwGridHeaderBgBrush", "#DFE2E6");
                SetBrush("SwGridLinesBrush", "#DCDFE3");
                SetBrush("SwGridSelectedBgBrush", "#FAD4D4");
                SetBrush("SwGridSelectedTextBrush", "#5C0A0A");

                SetBrush("SwBannerBgBrush", "#EBF7EE");
                SetBrush("SwBannerBorderBrush", "#5CA974");
                SetBrush("SwBannerTextBrush", "#0F5128");
                SetBrush("SwBadgeBgBrush", "#FADCDC");
            }
        }

        private void SetBrush(string resourceKey, string hexColor)
        {
            var color = (Color)ColorConverter.ConvertFromString(hexColor);
            Resources[resourceKey] = new SolidColorBrush(color);
        }

        private void Mappings_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (DimensionMapping oldItem in e.OldItems)
                {
                    UnsubscribeMapping(oldItem);
                }
            }

            if (e.NewItems != null)
            {
                foreach (DimensionMapping newItem in e.NewItems)
                {
                    SubscribeMapping(newItem);
                }
            }

            if (DataContext is MainViewModel vm)
            {
                RebuildDataGridColumns(vm);
            }
        }

        private void SubscribeMapping(DimensionMapping mapping)
        {
            if (mapping == null) return;
            mapping.PropertyChanged -= Mapping_PropertyChanged;
            mapping.PropertyChanged += Mapping_PropertyChanged;
            mapping.AliasRenamed -= Mapping_AliasRenamed;
            mapping.AliasRenamed += Mapping_AliasRenamed;
        }

        private void UnsubscribeMapping(DimensionMapping mapping)
        {
            if (mapping == null) return;
            mapping.PropertyChanged -= Mapping_PropertyChanged;
            mapping.AliasRenamed -= Mapping_AliasRenamed;
        }

        private void Mapping_AliasRenamed(string oldAlias, string newAlias)
        {
            if (DataContext is MainViewModel vm && vm.Rows != null)
            {
                foreach (var row in vm.Rows)
                {
                    if (row.Values.TryGetValue(oldAlias, out double val))
                    {
                        row.Values.Remove(oldAlias);
                        row.Values[newAlias] = val;
                    }
                }
            }
        }

        private void Mapping_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DimensionMapping.Alias))
            {
                if (DataContext is MainViewModel vm)
                {
                    RebuildDataGridColumns(vm);
                }
            }
        }

        /// <summary>
        /// Recria as colunas de cotas dinâmicas no DataGrid com base nos Apelidos (IA) definidos na Aba 1
        /// </summary>
        public void RebuildDataGridColumns(MainViewModel vm)
        {
            if (VariationsDataGrid == null || vm == null) return;

            VariationsDataGrid.Columns.Clear();

            // 1. Coluna SKU (Editável)
            var skuCol = new DataGridTextColumn
            {
                Header = "SKU / Código",
                Binding = new Binding("Sku")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                },
                FontWeight = FontWeights.SemiBold,
                MinWidth = 105,
                Width = 120
            };
            VariationsDataGrid.Columns.Add(skuCol);

            // 2. Coluna para cada Apelido (IA) definido pelo usuário na Aba 1
            foreach (var mapping in vm.Mappings)
            {
                string aliasName = string.IsNullOrWhiteSpace(mapping.Alias) ? mapping.FullName : mapping.Alias;

                var col = new DataGridTextColumn
                {
                    Header = $"{aliasName} (mm)",
                    Binding = new Binding($"[{aliasName}]")
                    {
                        Mode = BindingMode.TwoWay,
                        StringFormat = "{0:F2}",
                        UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
                        FallbackValue = 0.0,
                        TargetNullValue = 0.0
                    },
                    MinWidth = 85,
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                };

                VariationsDataGrid.Columns.Add(col);
            }

            // 3. Coluna de Status da Geração
            var statusCol = new DataGridTextColumn
            {
                Header = "Status",
                Binding = new Binding("StatusMessage"),
                IsReadOnly = true,
                MinWidth = 90,
                Width = 105
            };
            VariationsDataGrid.Columns.Add(statusCol);
        }

        #region Drag and Drop para arquivos

        private void DropZoneBorder_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void DropZoneBorder_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    string filePath = files[0];
                    if (DataContext is MainViewModel vm)
                    {
                        vm.InputFilePath = filePath;
                        vm.AddLog(LogType.Info, $"Arquivo carregado via Drag & Drop: {Path.GetFileName(filePath)}");
                    }
                }
            }
            e.Handled = true;
        }

        #endregion
    }
}
