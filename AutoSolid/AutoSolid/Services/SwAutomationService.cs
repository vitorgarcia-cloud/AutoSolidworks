using System;
using System.IO;
using System.Runtime.InteropServices;
using AutoSolid.Core;
using AutoSolid.Models;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AutoSolid.Services
{
    public class SwAutomationService
    {
        private readonly SldWorks _swApp;

        public SwAutomationService(SldWorks swApp)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
        }

        public SldWorks SwApp => _swApp;

        /// <summary>
        /// Obtém o documento de peça ativo no SolidWorks.
        /// Retorna null se não houver documento ou se não for uma peça (.sldprt).
        /// </summary>
        public ModelDoc2 GetActivePartDocument()
        {
            ModelDoc2 activeDoc = _swApp.IActiveDoc2;
            if (activeDoc == null) return null;

            if (activeDoc.GetType() != (int)swDocumentTypes_e.swDocPART)
            {
                return null;
            }

            return activeDoc;
        }

        /// <summary>
        /// Limpa a seleção atual no SolidWorks para iniciar o modo interativo de escolha de cota/extrusão.
        /// </summary>
        public void ClearActiveSelection()
        {
            ModelDoc2 doc = GetActivePartDocument();
            if (doc != null)
            {
                doc.ClearSelection2(true);
            }
        }

        /// <summary>
        /// Inspeciona silenciosamente a seleção atual no SolidWorks (sem lançar exceção se nada estiver selecionado).
        /// Suporta:
        /// 1. Cotas de esboço (Sketch Dimensions) e Cotas de recursos 3D/Extrusão clicadas na tela (swSelDIMENSIONS).
        /// 2. Recursos 3D (Extrusão, Corte, Revolução, etc.) clicados na árvore de projeto (swSelBODYFEATURES).
        /// 3. Faces 3D de uma extrusão clicadas diretamente no modelo (swSelFACES -> Feature -> DisplayDimension).
        /// </summary>
        public DimensionMapping TryGetCurrentlySelectedDimension()
        {
            ModelDoc2 doc = GetActivePartDocument();
            if (doc == null) return null;

            SelectionMgr selMgr = null;
            try
            {
                selMgr = (SelectionMgr)doc.SelectionManager;
                if (selMgr == null) return null;

                int count = selMgr.GetSelectedObjectCount2(-1);
                if (count <= 0) return null;

                // 1ª Prioridade: Procura se algum objeto selecionado é diretamente uma Cota (Dimension de Sketch ou de Extrusão)
                for (int i = count; i >= 1; i--)
                {
                    int selType = selMgr.GetSelectedObjectType3(i, -1);
                    if (selType == (int)swSelectType_e.swSelDIMENSIONS)
                    {
                        DisplayDimension dispDim = null;
                        try
                        {
                            dispDim = (DisplayDimension)selMgr.GetSelectedObject6(i, -1);
                            var mapping = ExtractMappingFromDisplayDimension(dispDim);
                            if (mapping != null) return mapping;
                        }
                        finally
                        {
                            object objDisp = dispDim;
                            ComReleaseHelper.Release(ref objDisp);
                        }
                    }
                }

                // 2ª Prioridade: Usuário clicou no recurso de Extrusão / Recurso 3D na árvore (swSelBODYFEATURES) ou Sketch (swSelSKETCHES)
                for (int i = count; i >= 1; i--)
                {
                    int selType = selMgr.GetSelectedObjectType3(i, -1);
                    if (selType == (int)swSelectType_e.swSelBODYFEATURES || selType == (int)swSelectType_e.swSelSKETCHES)
                    {
                        Feature feat = null;
                        try
                        {
                            feat = (Feature)selMgr.GetSelectedObject6(i, -1);
                            var mapping = ExtractPrimaryDimensionFromFeature(feat);
                            if (mapping != null) return mapping;
                        }
                        finally
                        {
                            object objFeat = feat;
                            ComReleaseHelper.Release(ref objFeat);
                        }
                    }
                }

                // 3ª Prioridade: Usuário clicou em uma Face 3D de uma Extrusão na viewport (swSelFACES)
                for (int i = count; i >= 1; i--)
                {
                    int selType = selMgr.GetSelectedObjectType3(i, -1);
                    if (selType == (int)swSelectType_e.swSelFACES)
                    {
                        Face2 face = null;
                        Feature feat = null;
                        try
                        {
                            face = (Face2)selMgr.GetSelectedObject6(i, -1);
                            if (face != null)
                            {
                                feat = (Feature)face.GetFeature();
                                var mapping = ExtractPrimaryDimensionFromFeature(feat);
                                if (mapping != null) return mapping;
                            }
                        }
                        finally
                        {
                            object objFeat = feat;
                            object objFace = face;
                            ComReleaseHelper.Release(ref objFeat);
                            ComReleaseHelper.Release(ref objFace);
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                object objSelMgr = selMgr;
                ComReleaseHelper.Release(ref objSelMgr);
            }
        }

        private DimensionMapping ExtractPrimaryDimensionFromFeature(Feature feat)
        {
            if (feat == null) return null;

            DisplayDimension dispDim = null;
            try
            {
                dispDim = (DisplayDimension)feat.GetFirstDisplayDimension();
                if (dispDim != null)
                {
                    return ExtractMappingFromDisplayDimension(dispDim);
                }
                return null;
            }
            finally
            {
                object objDisp = dispDim;
                ComReleaseHelper.Release(ref objDisp);
            }
        }

        private DimensionMapping ExtractMappingFromDisplayDimension(DisplayDimension dispDim)
        {
            if (dispDim == null) return null;

            Dimension dim = null;
            try
            {
                dim = dispDim.GetDimension2(0);
                if (dim == null) return null;

                string fullName = dim.FullName; // ex: "D1@Esboço1" ou "D1@Ressalto-extrusão1"
                double valorMetros = dim.SystemValue;
                double valorMm = Math.Round(valorMetros * 1000.0, 4);

                string baseName = fullName.Contains("@") ? fullName.Split('@')[0] : fullName;
                string featurePart = fullName.Contains("@") ? fullName.Split('@')[1] : string.Empty;

                // Se for uma extrusão cujo nome da cota padrão é apenas "D1", sugere um apelido intuitivo ou mantém o nome configurado
                string defaultAlias;
                if (string.Equals(baseName, "D1", StringComparison.OrdinalIgnoreCase) &&
                    (featurePart.IndexOf("extrus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     featurePart.IndexOf("ressalto", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     featurePart.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    defaultAlias = "ESPESSURA_EXT";
                }
                else
                {
                    defaultAlias = System.Text.RegularExpressions.Regex.Replace(baseName, @"[^\w]", "_").Trim('_').ToUpperInvariant();
                }

                if (string.IsNullOrEmpty(defaultAlias)) defaultAlias = "COTA";

                return new DimensionMapping
                {
                    FullName = fullName,
                    Alias = defaultAlias,
                    CurrentValueMm = valorMm,
                    Unit = "mm"
                };
            }
            finally
            {
                object objDim = dim;
                ComReleaseHelper.Release(ref objDim);
            }
        }

        /// <summary>
        /// Captura a cota selecionada pelo usuário na viewport ou na FeatureTree (lançando mensagem amigável se vazia).
        /// </summary>
        public DimensionMapping CaptureSelectedDimension()
        {
            ModelDoc2 doc = GetActivePartDocument();
            if (doc == null)
            {
                throw new InvalidOperationException("Nenhum arquivo de peça (.sldprt) está ativo no SolidWorks.");
            }

            var mapping = TryGetCurrentlySelectedDimension();
            if (mapping == null)
            {
                throw new InvalidOperationException("Selecione uma cota de esboço ou uma extrusão no modelo 3D / árvore de projeto.");
            }

            return mapping;
        }

        /// <summary>
        /// Altera o valor de uma cota no modelo (seja de esboço ou de extrusão/recurso 3D).
        /// Converte mm para metros do SI.
        /// </summary>
        public bool SetDimensionValue(ModelDoc2 doc, string fullDimName, double valueMm)
        {
            if (doc == null || string.IsNullOrWhiteSpace(fullDimName)) return false;

            Dimension dim = null;
            try
            {
                dim = (Dimension)doc.Parameter(fullDimName);
                if (dim == null) return false;

                // Converte mm para metros (unidade padrão do SI na API SolidWorks)
                double valorMetros = valueMm * 0.001;

                int result = dim.SetSystemValue3(
                    valorMetros,
                    (int)swSetValueInConfiguration_e.swSetValue_InAllConfigurations,
                    null
                );

                return result == (int)swSetValueReturnStatus_e.swSetValue_Successful;
            }
            finally
            {
                object objDim = dim;
                ComReleaseHelper.Release(ref objDim);
            }
        }

        /// <summary>
        /// Força a reconstrução do modelo 3D após a aplicação de todas as cotas da variação.
        /// </summary>
        public bool RebuildModel(ModelDoc2 doc)
        {
            if (doc == null) return false;
            return doc.ForceRebuild3(false);
        }

        /// <summary>
        /// Percorre a árvore de recursos da peça para verificar se algum recurso entrou em estado de erro
        /// após a reconstrução geométrica.
        /// </summary>
        public bool HasGeometryErrors(ModelDoc2 doc, out string errorReport)
        {
            errorReport = string.Empty;
            if (doc == null) return false;

            Feature feat = (Feature)doc.FirstFeature();
            bool hasError = false;

            while (feat != null)
            {
                bool isWarning = false;
                int errorCode = feat.GetErrorCode2(out isWarning);
                if (errorCode != 0 && !isWarning)
                {
                    hasError = true;
                    errorReport += $"[Recurso '{feat.Name}' falhou com código de erro {errorCode}] ";
                }

                Feature nextFeat = (Feature)feat.GetNextFeature();
                object objOldFeat = feat;
                ComReleaseHelper.Release(ref objOldFeat);
                feat = nextFeat;
            }

            return hasError;
        }

        /// <summary>
        /// Salva uma cópia silenciosa da peça ativa sem alterar o arquivo original em edição.
        /// </summary>
        public bool SaveSilentCopy(ModelDoc2 doc, string targetFilePath, out int errors, out int warnings)
        {
            errors = 0;
            warnings = 0;

            if (doc == null || string.IsNullOrWhiteSpace(targetFilePath)) return false;

            string targetDir = Path.GetDirectoryName(targetFilePath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            int saveOptions = (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (int)swSaveAsOptions_e.swSaveAsOptions_Copy;

            bool success = doc.Extension.SaveAs(
                targetFilePath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                saveOptions,
                null,
                ref errors,
                ref warnings
            );

            return success;
        }
    }
}
