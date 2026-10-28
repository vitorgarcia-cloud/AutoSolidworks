using System;
using System.Collections.Generic;
using AutoSolid.Core;

namespace AutoSolid.Models
{
    public enum GenerationStatus
    {
        Pendente,
        Processando,
        Sucesso,
        ErroRebuild,
        ErroSalvamento
    }

    public class ParametricRow : ObservableObject
    {
        private string _sku;
        private string _baseSku;
        private bool _isUpdatingFromPrefix;
        private GenerationStatus _status = GenerationStatus.Pendente;
        private string _statusMessage = "Pendente";

        /// <summary>
        /// Nome base original extraído da tabela (ex: "M5 x 15") sem o prefixo global
        /// </summary>
        public string BaseSku
        {
            get => _baseSku;
            set => _baseSku = value;
        }

        /// <summary>
        /// Código ou nome final da peça (nome do arquivo .SLDPRT final, já com prefixo se houver)
        /// </summary>
        public string Sku
        {
            get => _sku;
            set
            {
                if (SetProperty(ref _sku, value))
                {
                    if (!_isUpdatingFromPrefix)
                    {
                        _baseSku = value;
                    }
                }
            }
        }

        /// <summary>
        /// Aplica dinamicamente o prefixo global ao BaseSku desta linha
        /// </summary>
        public void ApplyPrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(_baseSku))
            {
                _baseSku = _sku ?? string.Empty;
            }

            string cleanPrefix = prefix?.Trim() ?? string.Empty;
            string cleanBase = _baseSku?.Trim() ?? string.Empty;

            _isUpdatingFromPrefix = true;
            try
            {
                if (string.IsNullOrEmpty(cleanPrefix))
                {
                    Sku = cleanBase;
                }
                else if (cleanBase.StartsWith(cleanPrefix + " ", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(cleanBase, cleanPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    Sku = cleanBase;
                }
                else
                {
                    Sku = $"{cleanPrefix} {cleanBase}".Trim();
                }
            }
            finally
            {
                _isUpdatingFromPrefix = false;
            }
        }

        /// <summary>
        /// Dicionário com os valores das cotas (Chave = Alias amigável, Valor = Medida em mm)
        /// </summary>
        public Dictionary<string, double> Values { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        public GenerationStatus Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        /// <summary>
        /// Indexador direto para binding WPF via sintaxe "[ALIAS]"
        /// </summary>
        public double this[string alias]
        {
            get
            {
                if (string.IsNullOrWhiteSpace(alias)) return 0.0;
                if (Values.TryGetValue(alias, out var val)) return val;

                // Tenta sem underlines ou comparando sem case
                foreach (var kvp in Values)
                {
                    if (string.Equals(kvp.Key.Replace("_", ""), alias.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
                        return kvp.Value;
                }
                return 0.0;
            }
            set
            {
                if (string.IsNullOrWhiteSpace(alias)) return;
                Values[alias] = value;
                OnPropertyChanged("Item[]");
            }
        }
    }
}
