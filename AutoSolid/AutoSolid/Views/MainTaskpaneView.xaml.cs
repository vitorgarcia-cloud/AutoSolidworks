using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AutoSolid.Models;
using AutoSolid.ViewModels;

namespace AutoSolid.Views
{
    public partial class MainTaskpaneView : UserControl
    {
        public MainTaskpaneView()
        {
            InitializeComponent();
            DataContextChanged += MainTaskpaneView_DataContextChanged;
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
                RebuildDataGridColumns(newVm);
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is MainViewModel vm && e.PropertyName == nameof(MainViewModel.SelectedTabIndex))
            {
                // Se o usuário acabou de avançar para a Aba 2, garante que qualquer edição na Aba 1 foi confirmada e reconstrói as colunas da tabela
                if (vm.SelectedTabIndex == 1)
                {
                    MappingsDataGrid?.CommitEdit(DataGridEditingUnit.Cell, true);
                    MappingsDataGrid?.CommitEdit(DataGridEditingUnit.Row, true);
                    RebuildDataGridColumns(vm);
                }
            }
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
            // Se já houver linhas na tabela quando o usuário mudar o Apelido (IA), migra os valores para o novo apelido
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
