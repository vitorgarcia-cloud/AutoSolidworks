using System;
using AutoSolid.Core;

namespace AutoSolid.Models
{
    public class DimensionMapping : ObservableObject
    {
        private string _fullName;
        private string _alias;
        private double _currentValueMm;
        private string _unit = "mm";

        /// <summary>
        /// Evento disparado quando o apelido (Alias) é alterado pelo usuário (OldAlias, NewAlias)
        /// </summary>
        public event Action<string, string> AliasRenamed;

        /// <summary>
        /// Nome completo da cota na SolidWorks API (ex: "D1@Esboço1" ou "D1@Sketch1")
        /// </summary>
        public string FullName
        {
            get => _fullName;
            set => SetProperty(ref _fullName, value);
        }

        /// <summary>
        /// Apelido amigável usado para mapeamento com colunas da IA (ex: "D_EXT", "ESPESSURA")
        /// </summary>
        public string Alias
        {
            get => _alias;
            set
            {
                string oldAlias = _alias;
                string cleanNew = value?.Trim() ?? string.Empty;
                if (SetProperty(ref _alias, cleanNew))
                {
                    if (!string.IsNullOrEmpty(oldAlias) && !string.IsNullOrEmpty(cleanNew) && !string.Equals(oldAlias, cleanNew, StringComparison.Ordinal))
                    {
                        AliasRenamed?.Invoke(oldAlias, cleanNew);
                    }
                }
            }
        }

        /// <summary>
        /// Valor numérico atual em milímetros lido da peça aberta
        /// </summary>
        public double CurrentValueMm
        {
            get => _currentValueMm;
            set => SetProperty(ref _currentValueMm, value);
        }

        public string Unit
        {
            get => _unit;
            set => SetProperty(ref _unit, value);
        }
    }
}
