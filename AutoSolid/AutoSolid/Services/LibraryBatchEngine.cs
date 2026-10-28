using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoSolid.Core;
using AutoSolid.Models;
using SolidWorks.Interop.sldworks;

namespace AutoSolid.Services
{
    public class BatchProgressReport
    {
        public int CurrentIndex { get; set; }
        public int TotalCount { get; set; }
        public double ExactPercentage { get; set; }
        public string CurrentSku { get; set; }
        public string StageDetail { get; set; }
        public double ElapsedSeconds { get; set; }
        public double EstimatedRemainingSeconds { get; set; }
        public double AverageSecondsPerItem { get; set; }
        public ProcessingLog LogEntry { get; set; }
    }

    public class LibraryBatchEngine
    {
        private readonly SwAutomationService _swService;

        public LibraryBatchEngine(SwAutomationService swService)
        {
            _swService = swService ?? throw new ArgumentNullException(nameof(swService));
        }

        /// <summary>
        /// Processa a geração em lote das variações paramétricas com telemetria detalhada de tempo e etapas.
        /// </summary>
        public async Task RunBatchAsync(
            IList<DimensionMapping> mappings,
            IList<ParametricRow> rows,
            string outputDirectory,
            IProgress<BatchProgressReport> progress,
            CancellationToken cancellationToken)
        {
            if (mappings == null || mappings.Count == 0)
                throw new InvalidOperationException("Nenhuma cota mapeada para processar.");

            if (rows == null || rows.Count == 0)
                throw new InvalidOperationException("Nenhuma linha de variação encontrada na tabela.");

            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("Diretório de destino não configurado.");

            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            ModelDoc2 activeDoc = _swService.GetActivePartDocument();
            if (activeDoc == null)
            {
                throw new InvalidOperationException("O arquivo de peça (.sldprt) modelo deve estar aberto no SolidWorks.");
            }

            var aliasToFullName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in mappings)
            {
                if (!string.IsNullOrWhiteSpace(m.Alias) && !string.IsNullOrWhiteSpace(m.FullName))
                {
                    aliasToFullName[m.Alias] = m.FullName;
                }
            }

            int total = rows.Count;
            var batchStopwatch = Stopwatch.StartNew();

            await Task.Run(() =>
            {
                for (int i = 0; i < total; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var row = rows[i];
                    row.Status = GenerationStatus.Processando;
                    row.StatusMessage = "Alterando cotas...";

                    double elapsed = batchStopwatch.Elapsed.TotalSeconds;
                    double avgPerItem = i > 0 ? elapsed / i : 2.0; // Estimativa inicial de 2.0s na primeira peça
                    double remaining = Math.Max(0, (total - i) * avgPerItem);

                    // Sub-etapa 1: Alterando cotas (25% da peça atual)
                    progress?.Report(new BatchProgressReport
                    {
                        CurrentIndex = i + 1,
                        TotalCount = total,
                        ExactPercentage = ((i + 0.2) / total) * 100.0,
                        CurrentSku = row.Sku,
                        StageDetail = $"[1/3] Aplicando cotas em '{row.Sku}' ({i + 1}/{total})...",
                        ElapsedSeconds = elapsed,
                        EstimatedRemainingSeconds = remaining,
                        AverageSecondsPerItem = avgPerItem,
                        LogEntry = new ProcessingLog
                        {
                            Type = LogType.Info,
                            Message = $"Iniciando peça: {row.Sku} ({i + 1}/{total})"
                        }
                    });

                    foreach (var kvp in row.Values)
                    {
                        string alias = kvp.Key;
                        double valMm = kvp.Value;

                        if (aliasToFullName.TryGetValue(alias, out string fullName))
                        {
                            bool mudou = _swService.SetDimensionValue(activeDoc, fullName, valMm);
                            if (!mudou)
                            {
                                progress?.Report(new BatchProgressReport
                                {
                                    CurrentIndex = i + 1,
                                    TotalCount = total,
                                    ExactPercentage = ((i + 0.3) / total) * 100.0,
                                    CurrentSku = row.Sku,
                                    StageDetail = $"Aviso na cota {alias}",
                                    ElapsedSeconds = batchStopwatch.Elapsed.TotalSeconds,
                                    EstimatedRemainingSeconds = remaining,
                                    AverageSecondsPerItem = avgPerItem,
                                    LogEntry = new ProcessingLog
                                    {
                                        Type = LogType.Warning,
                                        Message = $"Aviso: Não foi possível alterar a cota '{fullName}' ({alias}) para {valMm} mm."
                                    }
                                });
                            }
                        }
                    }

                    // Sub-etapa 2: Rebuild 3D (60% da peça atual)
                    row.StatusMessage = "Reconstruindo 3D...";
                    elapsed = batchStopwatch.Elapsed.TotalSeconds;
                    remaining = Math.Max(0, (total - (i + 0.5)) * avgPerItem);

                    progress?.Report(new BatchProgressReport
                    {
                        CurrentIndex = i + 1,
                        TotalCount = total,
                        ExactPercentage = ((i + 0.6) / total) * 100.0,
                        CurrentSku = row.Sku,
                        StageDetail = $"[2/3] Reconstruindo geometria 3D de '{row.Sku}'...",
                        ElapsedSeconds = elapsed,
                        EstimatedRemainingSeconds = remaining,
                        AverageSecondsPerItem = avgPerItem
                    });

                    _swService.RebuildModel(activeDoc);

                    // Verifica integridade geométrica (Feature.GetErrorCode2)
                    string errorReport;
                    bool hasErrors = _swService.HasGeometryErrors(activeDoc, out errorReport);

                    if (hasErrors)
                    {
                        row.Status = GenerationStatus.ErroRebuild;
                        row.StatusMessage = "Erro de geometria: " + errorReport;

                        elapsed = batchStopwatch.Elapsed.TotalSeconds;
                        avgPerItem = elapsed / (i + 1);
                        remaining = Math.Max(0, (total - (i + 1)) * avgPerItem);

                        progress?.Report(new BatchProgressReport
                        {
                            CurrentIndex = i + 1,
                            TotalCount = total,
                            ExactPercentage = ((double)(i + 1) / total) * 100.0,
                            CurrentSku = row.Sku,
                            StageDetail = $"Erro geométrico em '{row.Sku}'",
                            ElapsedSeconds = elapsed,
                            EstimatedRemainingSeconds = remaining,
                            AverageSecondsPerItem = avgPerItem,
                            LogEntry = new ProcessingLog
                            {
                                Type = LogType.Error,
                                Message = $"Falha de geometria em '{row.Sku}': {errorReport}"
                            }
                        });
                        continue;
                    }

                    // Sub-etapa 3: Salvando cópia .SLDPRT (90% -> 100% da peça atual)
                    row.StatusMessage = "Salvando arquivo...";
                    string safeFileName = MakeValidFileName(row.Sku) + ".SLDPRT";
                    string destPath = Path.Combine(outputDirectory, safeFileName);

                    int saveErrors = 0;
                    int saveWarnings = 0;

                    bool saved = _swService.SaveSilentCopy(activeDoc, destPath, out saveErrors, out saveWarnings);

                    elapsed = batchStopwatch.Elapsed.TotalSeconds;
                    avgPerItem = elapsed / (i + 1);
                    remaining = Math.Max(0, (total - (i + 1)) * avgPerItem);

                    if (saved && File.Exists(destPath))
                    {
                        row.Status = GenerationStatus.Sucesso;
                        row.StatusMessage = "Salvo com sucesso";

                        progress?.Report(new BatchProgressReport
                        {
                            CurrentIndex = i + 1,
                            TotalCount = total,
                            ExactPercentage = ((double)(i + 1) / total) * 100.0,
                            CurrentSku = row.Sku,
                            StageDetail = $"[3/3] '{safeFileName}' salvo ({i + 1}/{total})!",
                            ElapsedSeconds = elapsed,
                            EstimatedRemainingSeconds = remaining,
                            AverageSecondsPerItem = avgPerItem,
                            LogEntry = new ProcessingLog
                            {
                                Type = LogType.Success,
                                Message = $"Peça '{safeFileName}' gerada e salva ({avgPerItem:F1}s/peça)."
                            }
                        });
                    }
                    else
                    {
                        row.Status = GenerationStatus.ErroSalvamento;
                        row.StatusMessage = $"Erro ao salvar (Cod: {saveErrors})";

                        progress?.Report(new BatchProgressReport
                        {
                            CurrentIndex = i + 1,
                            TotalCount = total,
                            ExactPercentage = ((double)(i + 1) / total) * 100.0,
                            CurrentSku = row.Sku,
                            StageDetail = $"Erro ao salvar '{safeFileName}'",
                            ElapsedSeconds = elapsed,
                            EstimatedRemainingSeconds = remaining,
                            AverageSecondsPerItem = avgPerItem,
                            LogEntry = new ProcessingLog
                            {
                                Type = LogType.Error,
                                Message = $"Falha ao salvar peça '{safeFileName}'. Código de erro: {saveErrors}"
                            }
                        });
                    }

                    ComReleaseHelper.CollectGarbage();
                }
            }, cancellationToken);
        }

        private string MakeValidFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "PECA_SEM_NOME";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name.Trim();
        }
    }
}
