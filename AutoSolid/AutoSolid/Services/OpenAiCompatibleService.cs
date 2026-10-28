using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using AutoSolid.Models;

namespace AutoSolid.Services
{
    public class OpenAiCompatibleService
    {
        private static readonly HttpClient _httpClient;
        private readonly JavaScriptSerializer _serializer;

        static OpenAiCompatibleService()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(120)
            };
        }

        public OpenAiCompatibleService()
        {
            _serializer = new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue
            };
        }

        /// <summary>
        /// Extrai linhas paramétricas de tabelas usando APIs compatíveis com OpenAI (GPT ou DeepSeek).
        /// </summary>
        public async Task<List<ParametricRow>> ExtractDataAsync(
            string filePath,
            IList<DimensionMapping> mappings,
            string apiKey,
            string model,
            string endpointUrl = "https://api.openai.com/v1/chat/completions",
            Action<double, string> onProgress = null,
            string userInstructions = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Arquivo de entrada não encontrado.", filePath);

            if (mappings == null || mappings.Count == 0)
                throw new ArgumentException("Pelo menos uma cota deve ser mapeada antes de extrair dados.", nameof(mappings));

            var dimensionAliases = mappings
                .Select(m => string.IsNullOrWhiteSpace(m.Alias) ? m.FullName : m.Alias)
                .ToList();

            string mappingsContextLines = string.Join("\n", mappings.Select(m =>
            {
                string alias = string.IsNullOrWhiteSpace(m.Alias) ? m.FullName : m.Alias;
                return $"- Apelido (IA): \"{alias}\" | Cota/Sketch no SolidWorks: \"{m.FullName}\" | Medida de referência no template: {m.CurrentValueMm:F2} mm";
            }));

            string cleanKey = SanitizeApiKey(apiKey);
            if (string.IsNullOrWhiteSpace(cleanKey))
                throw new ArgumentException("A chave de API não foi informada.", nameof(apiKey));

            string cleanModel = CleanModelName(model);

            onProgress?.Invoke(15, $"Preparando arquivo para envio ({cleanModel})...");

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            bool isImage = ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp";

            string userPromptSection = string.IsNullOrWhiteSpace(userInstructions)
                ? string.Empty
                : "\nINSTRUÇÕES ESPECÍFICAS DO USUÁRIO (PRIORIDADE MÁXIMA):\n" +
                  $"{userInstructions.Trim()}\n";

            string exampleItem = "{\"SKU\": \"MODELO_01\", " + string.Join(", ", dimensionAliases.Select(a => $"\"{a}\": 10.0")) + "}";
            string systemPrompt =
                "Você é um Engenheiro Mecânico Sênior e Especialista em CAD/SolidWorks capaz de interpretar catálogos, tabelas dimensionais e desenhos técnicos de QUALQUER componente mecânico (arruelas, borrachas/vedações, cubos, rodas, pneus, eixos, flanges, buchas, parafusos, porcas, etc.).\n" +
                "Sua função é analisar o desenho e a tabela do documento e ASSOCIAR as medidas do desenho/tabela com os Apelidos (IA) definidos pelo usuário no SolidWorks.\n" +
                "Cotas e Apelidos (IA) mapeados pelo usuário:\n" +
                $"{mappingsContextLines}\n" +
                "O usuário pode usar nomes usuais (ex: 'D_EXT', 'D_INT', 'ESPESSURA', 'LARGURA', 'ALTURA', 'CHAVE', 'COMPRIMENTO') ou o próprio símbolo do desenho/tabela (ex: 'd', 'd1', 'd2', 'D', 's', 'h', 'L', 'B', 't').\n" +
                userPromptSection +
                "Converta todas as medidas numéricas para milímetros (mm). Se estiver em polegadas, multiplique por 25.4.\n" +
                $"Retorne estritamente um objeto JSON no formato: {{\"items\": [{exampleItem}]}}. Sem texto fora do JSON.";

            var messages = new List<object>();
            bool isReasoningModel = cleanModel.StartsWith("o1") || cleanModel.StartsWith("o3") || cleanModel.Contains("reasoner");

            if (!isReasoningModel)
            {
                messages.Add(new Dictionary<string, object>
                {
                    ["role"] = "system",
                    ["content"] = systemPrompt
                });
            }

            if (isImage)
            {
                if (endpointUrl.Contains("deepseek"))
                {
                    throw new InvalidOperationException("A API do DeepSeek trabalha com dados de texto/CSV. Para imagens ou PDFs escaneados, utilize os modelos do Google Gemini ou GPT-4o-mini.");
                }

                byte[] fileBytes = File.ReadAllBytes(filePath);
                string base64 = Convert.ToBase64String(fileBytes);
                string mimeType = ext == ".png" ? "image/png" : "image/jpeg";

                var userContentList = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = (isReasoningModel ? systemPrompt + "\n\n" : "") +
                                   $"Extraia os valores das cotas ({string.Join(", ", dimensionAliases)}) da tabela contida nesta imagem."
                    },
                    new Dictionary<string, object>
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new Dictionary<string, object>
                        {
                            ["url"] = $"data:{mimeType};base64,{base64}"
                        }
                    }
                };

                messages.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = userContentList
                });
            }
            else
            {
                string fileContent;
                try
                {
                    fileContent = File.ReadAllText(filePath);
                }
                catch
                {
                    throw new InvalidOperationException("Não foi possível ler o arquivo como texto. Para PDFs nativos, utilize o Google Gemini.");
                }

                messages.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = (isReasoningModel ? systemPrompt + "\n\n" : "") +
                                  $"Conteúdo do documento:\n\n{fileContent}\n\nExtraia todas as variações para o SKU e as cotas: {string.Join(", ", dimensionAliases)}."
                });
            }

            var requestBody = new Dictionary<string, object>
            {
                ["model"] = cleanModel,
                ["messages"] = messages
            };

            if (!isReasoningModel)
            {
                requestBody["temperature"] = 0.1;
                requestBody["response_format"] = new Dictionary<string, object>
                {
                    ["type"] = "json_object"
                };
            }

            string jsonPayload = _serializer.Serialize(requestBody);

            onProgress?.Invoke(45, $"Conectando e aguardando resposta de {cleanModel}...");

            using (var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl))
            {
                request.Headers.Add("Authorization", $"Bearer {cleanKey}");
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await _httpClient.SendAsync(request).ConfigureAwait(false);
                string responseString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Erro na API ({response.StatusCode}): {responseString}");
                }

                onProgress?.Invoke(90, "Interpretando JSON retornado pela IA...");
                return ParseOpenAiResponse(responseString, dimensionAliases);
            }
        }

        private List<ParametricRow> ParseOpenAiResponse(string responseJson, IList<string> aliases)
        {
            var resultList = new List<ParametricRow>();
            var rootObj = _serializer.Deserialize<Dictionary<string, object>>(responseJson);

            if (rootObj == null || !rootObj.ContainsKey("choices"))
                throw new Exception("Resposta inválida da API.");

            var choices = rootObj["choices"] as System.Collections.ArrayList;
            if (choices == null || choices.Count == 0)
                throw new Exception("Nenhum resultado retornado.");

            var firstChoice = choices[0] as Dictionary<string, object>;
            var message = firstChoice["message"] as Dictionary<string, object>;
            string rawContent = message["content"]?.ToString()?.Trim();

            if (string.IsNullOrWhiteSpace(rawContent))
                throw new Exception("Conteúdo vazio retornado pelo modelo.");

            if (rawContent.StartsWith("```json")) rawContent = rawContent.Substring(7);
            if (rawContent.StartsWith("```")) rawContent = rawContent.Substring(3);
            if (rawContent.EndsWith("```")) rawContent = rawContent.Substring(0, rawContent.Length - 3);
            rawContent = rawContent.Trim();

            System.Collections.ArrayList items = null;

            if (rawContent.StartsWith("["))
            {
                items = _serializer.Deserialize<System.Collections.ArrayList>(rawContent);
            }
            else
            {
                var parsed = _serializer.Deserialize<Dictionary<string, object>>(rawContent);
                if (parsed != null)
                {
                    if (parsed.ContainsKey("items") && parsed["items"] is System.Collections.ArrayList arr)
                    {
                        items = arr;
                    }
                    else
                    {
                        foreach (var val in parsed.Values)
                        {
                            if (val is System.Collections.ArrayList innerArr)
                            {
                                items = innerArr;
                                break;
                            }
                        }
                    }
                }
            }

            if (items == null) return resultList;

            foreach (var item in items)
            {
                if (item is Dictionary<string, object> dict)
                {
                    string sku = dict.ContainsKey("SKU") ? dict["SKU"]?.ToString() : "PECA_SEM_NOME";
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

            if (normalizedAlias.Contains("DIAM") || normalizedAlias.Contains("ROSCA"))
            {
                if (!string.IsNullOrWhiteSpace(sku))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(sku, @"[Mm]\s*(\d+(?:[\.,]\d+)?)");
                    if (match.Success)
                    {
                        if (double.TryParse(match.Groups[1].Value.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double dVal))
                            return dVal;
                    }
                }
            }

            if (normalizedAlias.Contains("COMP") || normalizedAlias == "L")
            {
                return 25.0;
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

            var fracMatch = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+)\s*/\s*(\d+)");
            if (fracMatch.Success)
            {
                double num = double.Parse(fracMatch.Groups[1].Value);
                double den = double.Parse(fracMatch.Groups[2].Value);
                if (den > 0) return Math.Round((num / den) * 25.4, 2);
            }

            var matches = System.Text.RegularExpressions.Regex.Matches(s.Replace(",", "."), @"\d+(?:\.\d+)?");
            if (matches.Count > 0)
            {
                string lastNum = matches[matches.Count - 1].Value;
                if (double.TryParse(lastNum, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double result))
                    return result;
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
            if (string.IsNullOrWhiteSpace(model)) return "gpt-4o-mini";
            string m = model.Trim();
            int spaceIdx = m.IndexOf(' ');
            if (spaceIdx > 0) m = m.Substring(0, spaceIdx);
            return m.Trim();
        }
    }
}
