using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using AutoSolid.Models;

namespace AutoSolid.Services
{
    public class GeminiAiService
    {
        private static readonly HttpClient _httpClient;
        private readonly JavaScriptSerializer _serializer;

        static GeminiAiService()
        {
            // Garante TLS 1.2 e TLS 1.3 no .NET Framework 4.8 dentro do processo do SolidWorks
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(120)
            };
        }

        public GeminiAiService()
        {
            _serializer = new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue
            };
        }

        /// <summary>
        /// Consulta a API do Gemini e lista os modelos disponíveis na conta do usuário,
        /// priorizando modelos Flash / Lite (mais comuns e gratuitos).
        /// </summary>
        public async Task<List<string>> ListAvailableModelsAsync(string apiKey)
        {
            string cleanKey = SanitizeApiKey(apiKey);
            if (string.IsNullOrWhiteSpace(cleanKey))
                throw new ArgumentException("Informe a chave de API para listar os modelos da sua conta.", nameof(apiKey));

            string url = $"https://generativelanguage.googleapis.com/v1beta/models?key={cleanKey}";
            HttpResponseMessage response = await _httpClient.GetAsync(url).ConfigureAwait(false);
            string responseString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Erro ao consultar modelos da conta ({response.StatusCode}): {ExtractErrorMessage(responseString)}");
            }

            var modelList = new List<string>();
            var rootObj = _serializer.Deserialize<Dictionary<string, object>>(responseString);

            if (rootObj != null && rootObj.ContainsKey("models") && rootObj["models"] is System.Collections.ArrayList modelsArray)
            {
                foreach (var item in modelsArray)
                {
                    if (item is Dictionary<string, object> modelDict && modelDict.ContainsKey("name"))
                    {
                        string name = modelDict["name"]?.ToString() ?? "";
                        if (name.StartsWith("models/"))
                        {
                            name = name.Substring(7);
                        }

                        // Ignora modelos apenas de embedding, imagem ou áudio
                        if (name.Contains("embedding") || name.Contains("imagen") || name.Contains("veo") || name.Contains("aqa") || name.Contains("tts"))
                            continue;

                        if (modelDict.ContainsKey("supportedGenerationMethods") &&
                            modelDict["supportedGenerationMethods"] is System.Collections.ArrayList methods)
                        {
                            if (methods.Contains("generateContent"))
                            {
                                modelList.Add(name);
                            }
                        }
                    }
                }
            }

            // Ordena priorizando os modelos flash e lite (low cost / gratuitos)
            return modelList
                .OrderByDescending(m => m.Contains("3.8-flash"))
                .ThenByDescending(m => m.Contains("flash-lite"))
                .ThenByDescending(m => m.Contains("2.5-flash"))
                .ThenByDescending(m => m.Contains("flash"))
                .ToList();
        }

        /// <summary>
        /// Extrai linhas paramétricas de um arquivo PDF, imagem ou texto usando Gemini multimodal.
        /// Possui auto-fallback de modelo (caso dê 404 na conta gratuita) e auto-fallback de schema (caso dê 400).
        /// </summary>
        public async Task<List<ParametricRow>> ExtractDataAsync(
            string filePath,
            IList<DimensionMapping> mappings,
            string apiKey,
            string model = "gemini-3.8-flash",
            Action<double, string> onProgress = null,
            string userInstructions = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Arquivo de entrada não encontrado.", filePath);

            if (mappings == null || mappings.Count == 0)
                throw new ArgumentException("Mapeie pelo menos uma cota na Aba 1 antes de extrair dados.", nameof(mappings));

            string cleanKey = SanitizeApiKey(apiKey);
            if (string.IsNullOrWhiteSpace(cleanKey))
                throw new ArgumentException("A chave de API do Gemini não foi informada.", nameof(apiKey));

            onProgress?.Invoke(12, "Lendo e codificando arquivo para envio...");

            byte[] fileBytes = File.ReadAllBytes(filePath);
            string base64Data = Convert.ToBase64String(fileBytes);
            string mimeType = GetMimeType(filePath);

            string cleanModel = CleanModelName(model);

            // Lista de modelos candidatos para fallback automático caso o modelo escolhido não exista na conta
            var candidateModels = new List<string> { cleanModel };
            string[] fallbackDefaults = { "gemini-3.8-flash", "gemini-2.5-flash-lite", "gemini-2.5-flash", "gemini-2.0-flash-lite", "gemini-1.5-flash" };
            foreach (var fb in fallbackDefaults)
            {
                if (!candidateModels.Contains(fb, StringComparer.OrdinalIgnoreCase))
                    candidateModels.Add(fb);
            }

            string lastError = string.Empty;
            bool queriedAccountModels = false;

            for (int i = 0; i < candidateModels.Count; i++)
            {
                string currentModel = candidateModels[i];
                onProgress?.Invoke(25 + Math.Min(i * 10, 30), $"Conectando ao Gemini ({currentModel})...");

                // 1ª tentativa no modelo atual: Com Structured Output (JSON Schema)
                var result = await TryGenerateContentAsync(
                    currentModel, cleanKey, mimeType, base64Data, filePath, mappings, useStrictSchema: true, onProgress, userInstructions);

                if (result.Success)
                {
                    onProgress?.Invoke(92, $"Dados recebidos via {currentModel}! Montando tabela...");
                    return result.Rows;
                }

                // Se deu 400 Bad Request, tenta imediatamente no MESMO modelo sem o responseSchema rígido (modo JSON compatível)
                if (result.StatusCode == HttpStatusCode.BadRequest && !result.ErrorMessage.Contains("API_KEY_INVALID"))
                {
                    onProgress?.Invoke(50, $"Ajustando formato JSON para {currentModel}...");
                    var retryCompat = await TryGenerateContentAsync(
                        currentModel, cleanKey, mimeType, base64Data, filePath, mappings, useStrictSchema: false, onProgress, userInstructions);

                    if (retryCompat.Success)
                    {
                        onProgress?.Invoke(92, $"Dados recebidos via {currentModel}! Montando tabela...");
                        return retryCompat.Rows;
                    }
                    lastError = retryCompat.ErrorMessage;
                }
                else
                {
                    lastError = result.ErrorMessage;
                }

                // Se a chave de API for inválida ou bloqueada, para imediatamente e avisa o usuário
                if (result.StatusCode == HttpStatusCode.Forbidden ||
                    (result.ErrorMessage != null && result.ErrorMessage.Contains("API_KEY_INVALID")))
                {
                    throw new Exception($"Chave de API inválida ou sem permissão: {ExtractErrorMessage(result.ErrorMessage)}");
                }

                // Se deu 404 (NotFound), verifica se a própria mensagem do Google sugere o nome do modelo novo
                if (result.StatusCode == HttpStatusCode.NotFound)
                {
                    string suggested = ExtractSuggestedModelFromError(result.ErrorMessage);
                    if (!string.IsNullOrEmpty(suggested) && !candidateModels.Contains(suggested, StringComparer.OrdinalIgnoreCase))
                    {
                        candidateModels.Insert(i + 1, suggested);
                    }
                    else if (!queriedAccountModels)
                    {
                        // Consulta automaticamente os modelos reais disponíveis na conta gratuita do usuário
                        queriedAccountModels = true;
                        try
                        {
                            onProgress?.Invoke(35, "Buscando automaticamente o modelo ativo na sua conta...");
                            var accountModels = await ListAvailableModelsAsync(cleanKey).ConfigureAwait(false);
                            foreach (var accModel in accountModels.Take(4))
                            {
                                if (!candidateModels.Contains(accModel, StringComparer.OrdinalIgnoreCase))
                                {
                                    candidateModels.Insert(i + 1, accModel);
                                }
                            }
                        }
                        catch
                        {
                            // Ignora erro na listagem secundária e segue para o próximo candidato
                        }
                    }
                }
            }

            throw new Exception($"Não foi possível concluir a extração na API Gemini.\nDetalhes: {ExtractErrorMessage(lastError)}");
        }

        private class AttemptResult
        {
            public bool Success { get; set; }
            public HttpStatusCode StatusCode { get; set; }
            public string ErrorMessage { get; set; }
            public List<ParametricRow> Rows { get; set; }
        }

        private async Task<AttemptResult> TryGenerateContentAsync(
            string model,
            string apiKey,
            string mimeType,
            string base64Data,
            string filePath,
            IList<DimensionMapping> mappings,
            bool useStrictSchema,
            Action<double, string> onProgress,
            string userInstructions = null)
        {
            var dimensionAliases = mappings
                .Select(m => string.IsNullOrWhiteSpace(m.Alias) ? m.FullName : m.Alias)
                .ToList();

            string mappingsContextLines = string.Join("\n", mappings.Select(m =>
            {
                string alias = string.IsNullOrWhiteSpace(m.Alias) ? m.FullName : m.Alias;
                return $"- Apelido (IA): \"{alias}\" | Cota/Sketch original no SolidWorks: \"{m.FullName}\" | Medida de referência na peça-template atual: {m.CurrentValueMm:F2} mm";
            }));

            string exampleJson = "{\"SKU\": \"MODELO_01\", " + string.Join(", ", dimensionAliases.Select(a => $"\"{a}\": 10.0")) + "}";

            string userPromptSection = string.IsNullOrWhiteSpace(userInstructions)
                ? string.Empty
                : "\nINSTRUÇÕES ESPECÍFICAS DO USUÁRIO (PRIORIDADE MÁXIMA):\n" +
                  $"{userInstructions.Trim()}\n";

            string promptText =
                "Você é um Engenheiro Mecânico Sênior e Especialista em CAD/SolidWorks capaz de interpretar catálogos, tabelas dimensionais e desenhos técnicos de QUALQUER componente mecânico (arruelas, anéis de vedação/borrachas, cubos, rodas, pneus, eixos, flanges, buchas, parafusos de diversos tipos, porcas, perfis estruturais, etc.).\n\n" +
                "FUNÇÃO PRINCIPAL DA IA:\n" +
                "Analisar o desenho técnico, símbolos, cotas visuais e a tabela do arquivo enviado, e ASSOCIAR corretamente as medidas do desenho/tabela com os Apelidos (IA) definidos pelo usuário na etapa de mapeamento do SolidWorks.\n\n" +
                "COTAS E APELIDOS DEFINIDOS PELO USUÁRIO NA PEÇA-TEMPLATE:\n" +
                $"{mappingsContextLines}\n\n" +
                "DIRETRIZES DE ASSOCIAÇÃO INTELIGENTE (GENERALISTA):\n" +
                "1. O usuário definiu na coluna 'Apelido (IA)' nomes que podem ser termos usuais (ex: 'D_EXT', 'DIAM_INT', 'FURO', 'ESPESSURA', 'LARGURA', 'ALTURA', 'RAIO', 'CUBO', 'BANDA', 'CHAVE', 'PASSO', 'COMPRIMENTO') ou diretamente as letras/símbolos que aparecem no desenho ou cabeçalho da tabela (ex: 'd', 'd1', 'd2', 'D', 'B', 's', 'h', 'k', 'L', 't', 'e', 'W').\n" +
                "2. Inspecione visualmente o desenho esquemático (se houver na imagem/PDF) para identificar qual letra ou símbolo aponta para qual geometria da peça e cruze essa informação com as colunas/linhas da tabela.\n" +
                "3. Utilize tanto o 'Apelido (IA)' quanto o nome original do Sketch/Cota e a medida de referência da peça-template como contexto para entender exatamente o que cada cota representa na geometria.\n" +
                "4. Caso alguma medida solicitada pelo usuário seja fixa no desenho ou não varie na tabela, preencha com o valor constante indicado no documento ou mantenha a medida de referência da peça-template.\n" +
                userPromptSection + "\n" +
                "REGRAS DE FORMATAÇÃO E UNIDADES:\n" +
                "1. O campo 'SKU' deve conter o nome, código ou especificação de cada variação/modelo listado na tabela.\n" +
                "2. As chaves do JSON DEVEM SER EXATAMENTE OS APELIDOS (IA) FORNECIDOS PELO USUÁRIO: " + string.Join(", ", dimensionAliases) + ".\n" +
                "3. Todos os valores das cotas devem ser números reais (float/double) convertidos para MILÍMETROS (mm), usando ponto decimal (ex: 12.5, 6.0, 3.65). Se a tabela estiver em polegadas (ex: '1/4\"' ou '0.5\"'), converta para milímetros (1 pol = 25.4 mm).\n" +
                "4. NUNCA inclua letras, símbolos ou unidades ('mm', 'pol', 'M', 'Ø') dentro dos campos numéricos das cotas.\n\n" +
                $"Exemplo do formato JSON obrigatório:\n[{exampleJson}]\n" +
                "Retorne ESTRITAMENTE o array JSON puro, sem comentários e sem blocos markdown.";

            var partsList = new List<object>();

            // Se for arquivo texto/CSV, envia como texto; se for PDF ou Imagem, envia inlineData
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".csv" || ext == ".txt" || ext == ".json")
            {
                string textContent = File.ReadAllText(filePath);
                partsList.Add(new Dictionary<string, object>
                {
                    ["text"] = $"Dados da tabela:\n{textContent}\n\n{promptText}"
                });
            }
            else
            {
                partsList.Add(new Dictionary<string, object>
                {
                    ["inlineData"] = new Dictionary<string, object>
                    {
                        ["mimeType"] = mimeType,
                        ["data"] = base64Data
                    }
                });
                partsList.Add(new Dictionary<string, object>
                {
                    ["text"] = promptText
                });
            }

            var generationConfig = new Dictionary<string, object>
            {
                ["temperature"] = 0.1,
                ["responseMimeType"] = "application/json"
            };

            if (useStrictSchema)
            {
                var propertiesSchema = new Dictionary<string, object>
                {
                    ["SKU"] = new Dictionary<string, object>
                    {
                        ["type"] = "STRING",
                        ["description"] = "Código do produto ou identificador único da peça."
                    }
                };

                var requiredFields = new List<string> { "SKU" };
                foreach (var alias in dimensionAliases)
                {
                    propertiesSchema[alias] = new Dictionary<string, object>
                    {
                        ["type"] = "NUMBER",
                        ["description"] = $"Medida '{alias}' em milímetros (mm)."
                    };
                    requiredFields.Add(alias);
                }

                generationConfig["responseSchema"] = new Dictionary<string, object>
                {
                    ["type"] = "ARRAY",
                    ["items"] = new Dictionary<string, object>
                    {
                        ["type"] = "OBJECT",
                        ["properties"] = propertiesSchema,
                        ["required"] = requiredFields
                    }
                };
            }

            var requestBody = new Dictionary<string, object>
            {
                ["contents"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["parts"] = partsList
                    }
                },
                ["generationConfig"] = generationConfig
            };

            string jsonPayload = _serializer.Serialize(requestBody);
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

            onProgress?.Invoke(60, $"Aguardando resposta da IA ({model})...");

            using (var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json"))
            {
                HttpResponseMessage response = await _httpClient.PostAsync(url, content).ConfigureAwait(false);
                string responseString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return new AttemptResult
                    {
                        Success = false,
                        StatusCode = response.StatusCode,
                        ErrorMessage = responseString
                    };
                }

                try
                {
                    var rows = ParseGeminiResponse(responseString, dimensionAliases);
                    return new AttemptResult
                    {
                        Success = true,
                        StatusCode = response.StatusCode,
                        Rows = rows
                    };
                }
                catch (Exception ex)
                {
                    return new AttemptResult
                    {
                        Success = false,
                        StatusCode = HttpStatusCode.OK,
                        ErrorMessage = "Erro ao interpretar JSON da IA: " + ex.Message
                    };
                }
            }
        }

        private List<ParametricRow> ParseGeminiResponse(string responseJson, IList<string> aliases)
        {
            var resultList = new List<ParametricRow>();

            var rootObj = _serializer.Deserialize<Dictionary<string, object>>(responseJson);
            if (rootObj == null || !rootObj.ContainsKey("candidates"))
                throw new Exception("Resposta inválida da API Gemini (sem 'candidates').");

            var candidates = rootObj["candidates"] as System.Collections.ArrayList;
            if (candidates == null || candidates.Count == 0)
                throw new Exception("Nenhum resultado retornado pela IA.");

            var firstCandidate = candidates[0] as Dictionary<string, object>;
            if (firstCandidate == null || !firstCandidate.ContainsKey("content"))
                throw new Exception("Candidato da IA não contém conteúdo.");

            var contentObj = firstCandidate["content"] as Dictionary<string, object>;
            var parts = contentObj["parts"] as System.Collections.ArrayList;
            if (parts == null || parts.Count == 0)
                throw new Exception("Resposta da IA vazia.");

            var firstPart = parts[0] as Dictionary<string, object>;
            string rawOutputJson = firstPart["text"]?.ToString()?.Trim();

            if (string.IsNullOrWhiteSpace(rawOutputJson))
                throw new Exception("Texto vazio retornado pelo modelo.");

            rawOutputJson = CleanMarkdownJson(rawOutputJson);

            System.Collections.ArrayList rowsArray = null;

            if (rawOutputJson.StartsWith("["))
            {
                rowsArray = _serializer.Deserialize<System.Collections.ArrayList>(rawOutputJson);
            }
            else if (rawOutputJson.StartsWith("{"))
            {
                var wrapperObj = _serializer.Deserialize<Dictionary<string, object>>(rawOutputJson);
                if (wrapperObj != null)
                {
                    foreach (var val in wrapperObj.Values)
                    {
                        if (val is System.Collections.ArrayList arr)
                        {
                            rowsArray = arr;
                            break;
                        }
                    }
                }
            }

            if (rowsArray == null) return resultList;

            foreach (var item in rowsArray)
            {
                if (item is Dictionary<string, object> dict)
                {
                    // Busca SKU de forma case-insensitive
                    string sku = "PECA_SEM_NOME";
                    foreach (var key in dict.Keys)
                    {
                        if (string.Equals(key, "SKU", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(key, "Nome", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(key, "Codigo", StringComparison.OrdinalIgnoreCase))
                        {
                            sku = dict[key]?.ToString() ?? sku;
                            break;
                        }
                    }

                    var row = new ParametricRow
                    {
                        Sku = sku,
                        Status = GenerationStatus.Pendente,
                        StatusMessage = "Pronto para gerar"
                    };

                    foreach (var alias in aliases)
                    {
                        double resolvedValue = ResolveDimensionValueFromDict(dict, alias, sku);
                        row.Values[alias] = resolvedValue;
                    }

                    resultList.Add(row);
                }
            }

            return resultList;
        }

        private static readonly Dictionary<string, string[]> CommonSynonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEXT"] = new[] { "D", "d2", "de", "d_ext", "diam_ext", "diametro_externo", "externo", "outer_diameter", "od" },
            ["DINT"] = new[] { "d", "d1", "di", "d_int", "diam_int", "diametro_interno", "furo", "interno", "inner_diameter", "id", "bore" },
            ["DIAM"] = new[] { "d", "D", "diametro", "rosca", "diam_rosca", "nominal", "diameter" },
            ["LARG"] = new[] { "B", "b", "W", "w", "s", "largura", "width", "chave", "sextavado", "banda" },
            ["TAMCAB"] = new[] { "s", "chave", "sextavado", "tam_cab", "largura", "entre_faces", "s (chave)", "s (chave) máx." },
            ["ALT"] = new[] { "h", "H", "k", "altura", "alt_cab", "altura_cabeca", "height" },
            ["COMP"] = new[] { "L", "l", "comprimento", "comp", "comp_util", "length" },
            ["ESP"] = new[] { "e", "t", "s", "h", "espessura", "thickness", "esp" },
            ["PASSO"] = new[] { "passo", "p", "rosca/passo", "pitch", "fpp" },
            ["RAIO"] = new[] { "r", "R", "raio", "radius" }
        };

        private double ResolveDimensionValueFromDict(Dictionary<string, object> dict, string alias, string sku)
        {
            if (dict == null || string.IsNullOrWhiteSpace(alias)) return 0.0;

            // 1. Procura direta pela chave exata ou sem underlines
            if (dict.ContainsKey(alias))
            {
                double d = ExtractCleanNumber(dict[alias]);
                if (d > 0) return d;
            }

            foreach (var key in dict.Keys)
            {
                if (string.Equals(key.Replace("_", ""), alias.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
                {
                    double d = ExtractCleanNumber(dict[key]);
                    if (d > 0) return d;
                }
            }

            // 2. Procura por sinônimos conhecidos da engenharia mecânica
            string normalizedAlias = alias.Replace("_", "").ToUpperInvariant();
            foreach (var kvp in CommonSynonyms)
            {
                string synKey = kvp.Key.Replace("_", "").ToUpperInvariant();
                if (normalizedAlias.Contains(synKey) || synKey.Contains(normalizedAlias))
                {
                    foreach (var syn in kvp.Value)
                    {
                        foreach (var key in dict.Keys)
                        {
                            string normDictKey = key.Replace("_", "").ToLowerInvariant();
                            string normSyn = syn.Replace("_", "").ToLowerInvariant();

                            if (normDictKey == normSyn || normDictKey.Contains(normSyn))
                            {
                                double d = ExtractCleanNumber(dict[key]);
                                if (d > 0) return d;
                            }
                        }
                    }
                }
            }

            // 3. Fallback especial para diâmetro de parafuso a partir do SKU (ex: "M5" -> 5.0, "M10" -> 10.0)
            if (normalizedAlias.Contains("DIAM") || normalizedAlias.Contains("ROSCA"))
            {
                if (!string.IsNullOrWhiteSpace(sku))
                {
                    var match = Regex.Match(sku, @"[Mm]\s*(\d+(?:[\.,]\d+)?)");
                    if (match.Success)
                    {
                        if (double.TryParse(match.Groups[1].Value.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double dVal))
                            return dVal;
                    }
                }
            }

            // 4. Fallback especial para Comprimento se não estiver no dict (adota padrão nominal seguro)
            if (normalizedAlias.Contains("COMP") || normalizedAlias == "L")
            {
                return 25.0; // Comprimento padrão de 25mm para não deixar peça com geometria zerada
            }

            return 0.0;
        }

        private double ExtractCleanNumber(object rawVal)
        {
            if (rawVal == null) return 0.0;

            if (rawVal is double d) return d;
            if (rawVal is int i) return (double)i;
            if (rawVal is float f) return (double)f;
            if (rawVal is decimal dec) return (double)dec;

            string s = rawVal.ToString().Trim();
            if (string.IsNullOrEmpty(s)) return 0.0;

            // Se for fração em polegadas simples (ex: "1/4" ou "3/16")
            var fracMatch = Regex.Match(s, @"^(\d+)\s*/\s*(\d+)");
            if (fracMatch.Success)
            {
                double num = double.Parse(fracMatch.Groups[1].Value);
                double den = double.Parse(fracMatch.Groups[2].Value);
                if (den > 0) return Math.Round((num / den) * 25.4, 2);
            }

            // Procura números na string (ex: "3,65 mm" -> 3.65; "M 10" -> 10; "5/16 - 7,90" -> 7.90)
            var matches = Regex.Matches(s.Replace(",", "."), @"\d+(?:\.\d+)?");
            if (matches.Count > 0)
            {
                string lastNum = matches[matches.Count - 1].Value;
                if (double.TryParse(lastNum, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double result))
                {
                    return result;
                }
            }

            return 0.0;
        }

        private string SanitizeApiKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            return key.Trim().Replace(" ", "").Replace("\r", "").Replace("\n", "");
        }

        private string CleanModelName(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return "gemini-3.8-flash";
            string m = model.Trim();
            // Caso o modelo na UI tenha uma descrição entre parênteses, ex: "gemini-3.8-flash (Gratuito)"
            int spaceIdx = m.IndexOf(' ');
            if (spaceIdx > 0) m = m.Substring(0, spaceIdx);
            if (m.StartsWith("models/")) m = m.Substring(7);
            return m.Trim();
        }

        private string ExtractSuggestedModelFromError(string errorJson)
        {
            if (string.IsNullOrWhiteSpace(errorJson)) return null;
            var match = Regex.Match(errorJson, @"use models/(gemini-[a-zA-Z0-9\.\-]+)");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            return null;
        }

        private string ExtractErrorMessage(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson)) return "Sem resposta do servidor.";
            try
            {
                var obj = _serializer.Deserialize<Dictionary<string, object>>(responseJson);
                if (obj != null && obj.ContainsKey("error") && obj["error"] is Dictionary<string, object> errDict)
                {
                    if (errDict.ContainsKey("message"))
                        return errDict["message"]?.ToString();
                }
            }
            catch { }
            return responseJson;
        }

        private string CleanMarkdownJson(string text)
        {
            text = text.Trim();
            if (text.StartsWith("```json")) text = text.Substring(7);
            else if (text.StartsWith("```")) text = text.Substring(3);
            if (text.EndsWith("```")) text = text.Substring(0, text.Length - 3);
            return text.Trim();
        }

        private string GetMimeType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".pdf": return "application/pdf";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                default: return "application/octet-stream";
            }
        }
    }
}
