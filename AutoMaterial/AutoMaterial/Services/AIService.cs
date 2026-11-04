using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using AutoMaterial.Models;

namespace AutoMaterial.Services
{
    /// <summary>
    /// Serviço de IA para geração autônoma de materiais no SolidWorks:
    /// - Força Structured Outputs (JSON Schema estrito) para garantir que NENHUMA propriedade venha zerada.
    /// - Classifica autonomamente o Tipo de Modelo Constitutivo do SolidWorks Simulation.
    /// - Seleciona famílias visuais de texturas (.jpg) e shaders (.p2m) oficiais do SolidWorks.
    /// - Valida e enriquece fisicamente todas as grandezas (conversão automática para SI em Pa e relações elásticas).
    /// </summary>
    public class AIService
    {
        private static readonly HttpClient _httpClient;
        private readonly JavaScriptSerializer _serializer;

        static AIService()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
        }

        public AIService()
        {
            _serializer = new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue
            };
        }

        private const string SystemPrompt =
            "Você é um Pesquisador Autônomo e Especialista Sênior em Ciência e Engenharia dos Materiais, Simulação CAE (SolidWorks Simulation / FEA) e Renderização 3D RealView/PhotoView 360.\n\n" +

            "OBJETIVO DA SUA TAREFA:\n" +
            "Pesquisar e determinar os dados técnicos, propriedades mecânicas, elásticas, térmicas e visuais REAIS do material solicitado pelo usuário, consultando mentalmente toda a literatura de engenharia, normas técnicas internacionais (ASTM, ISO, DIN, ABNT, SAE, AISI), bancos de dados de materiais (como MatWeb, ASM International, MMPDS) e datasheets técnicos de fabricantes.\n" +
            "Você NÃO deve inventar números aleatórios nem se prender a exemplos genéricos: investigue as propriedades específicas do material, liga, grau ou norma informada.\n\n" +

            "====================================================================\n" +
            "REGRAS DE SANIDADE FÍSICA E COERÊNCIA DE ENGENHARIA (O ÓBVIO QUE DEVE SER RESPEITADO):\n" +
            "====================================================================\n" +
            "1. COERÊNCIA DE MASSA ESPECÍFICA (DENSIDADE):\n" +
            "   - A densidade ('mass_density') deve ser rigorosamente compatível com a família química e estrutural do material pesquisado.\n" +
            "   - Por exemplo, um plástico/polímero, borracha ou madeira JAMAIS pode ser mais pesado ou denso que metais densos (como aço, chumbo ou ouro). Polímeros e madeiras têm densidades típicas em centenas ou poucos milhares de kg/m³, enquanto metais leves (magnésio, alumínio, titânio) e ligas pesadas (aços, cobre, chumbo, ouro) possuem ordens de grandeza conhecidas.\n" +
            "   - Unidade OBRIGATÓRIA: kg/m³. Se pesquisar em g/cm³, converta multiplicando por 1000 (ex: 1.05 g/cm³ = 1050 kg/m³).\n\n" +

            "2. UNIDADES DO SISTEMA INTERNACIONAL (SI) EXIGIDAS PELO SOLIDWORKS:\n" +
            "   - 'elastic_modulus' (EX - Módulo de Young): em N/m² (Pascal - Pa). Converta GPa (x 10^9) ou MPa (x 10^6) para Pascal puro.\n" +
            "   - 'poisson_ratio' (NUXY - Coeficiente de Poisson): Adimensional, fisicamente admissível entre 0.01 e 0.499.\n" +
            "   - 'shear_modulus' (GXY - Módulo de Cisalhamento): em N/m² (Pa), fisicamente coerente com G ≈ E / (2*(1+v)) para comportamento isotrópico.\n" +
            "   - 'tensile_strength' (SIGXT - Resistência à Tração): em N/m² (Pa). Converta MPa para Pascal (x 10^6).\n" +
            "   - 'compressive_strength' (SIGXC - Resistência à Compressão): em N/m² (Pa). Converta MPa para Pascal (x 10^6).\n" +
            "   - 'yield_strength' (SIGYLD - Limite de Escoamento / Limite Elástico Proporcional): em N/m² (Pa).\n" +
            "   - 'thermal_expansion_coefficient' (ALPX - Dilatação Térmica Linear): em 1/K (/K).\n" +
            "   - 'thermal_conductivity' (KX - Condutividade Térmica): em W/(m·K).\n" +
            "   - 'specific_heat' (C - Calor Específico): em J/(kg·K).\n" +
            "   - 'material_damping' (MDAMP - Amortecimento): Adimensional (positivo, tipicamente entre 0.005 e 0.15).\n\n" +

            "3. RELAÇÕES MECÂNICAS FUNDAMENTAIS E PROIBIÇÃO DE ZERO (0):\n" +
            "   - O Limite de Escoamento ('yield_strength') NUNCA pode ser superior ao Limite de Resistência à Tração ('tensile_strength'), pois o escoamento antecede a ruptura em tração.\n" +
            "   - O SolidWorks rejeita e invalida análises de simulação se qualquer uma das propriedades numéricas for zero (0). Portanto, TODAS as grandezas devem conter valores reais, positivos (> 0).\n" +
            "   - Para materiais frágeis, compósitos, madeiras, elastômeros ou cerâmicas que não possuem patamar de escoamento plástico metálico nítido, pesquise e forneça o Limite Elástico de Proporcionalidade ou a tensão de projeto admissível da norma em 'yield_strength' (JAMAIS retorne 0).\n\n" +

            "4. MODELO CONSTITUTIVO DO SOLIDWORKS SIMULATION ('model_type'):\n" +
            "   Classifique o material pesquisado na opção técnica do SolidWorks que descreve seu comportamento mecânico real:\n" +
            "   - 'Isotrópico linear elástico' (metais comuns, ligas, polímeros rígidos e cerâmicas em regime elástico)\n" +
            "   - 'Ortotrópico linear elástico' (madeiras naturais, MDF, compensados, laminados de fibra de carbono/vidro)\n" +
            "   - 'Elástico não linear' (materiais com curva elástica reversível não-linear)\n" +
            "   - 'Plasticidade - von Mises' (metais dúcteis sofrendo deformação plástica permanente sob escoamento)\n" +
            "   - 'Plasticidade - Tresca' (ligas metálicas avaliadas por tensão de cisalhamento máxima)\n" +
            "   - 'Plasticidade - Drucker Prager' (solos, rochas, concreto e polímeros sob pressão hidrostática)\n" +
            "   - 'Hiperelástico - Mooney Rivlin' (borrachas e elastômeros sintéticos sob grandes deformações elásticas)\n" +
            "   - 'Hiperelástico - Ogden' (borrachas sob tração severa)\n" +
            "   - 'Hiperelástico - Blatz Ko' (espumas compressíveis e borrachas celulares)\n" +
            "   - 'Viscoelástico' (polímeros sujeitos a fluência e relaxação no tempo)\n" +
            "   - 'Nitinol' (ligas com efeito térmico/mecânico de memória de forma)\n\n" +

            "5. FAMÍLIA DE APARÊNCIA E TEXTURA FOTORREALISTA ('appearance_family' e 'rgb_color'):\n" +
            "   Mapeie para a textura e shader nativo do SolidWorks mais próximo do aspecto físico real do material:\n" +
            "   - Madeiras: 'wood_beech' (MDF, claros), 'wood_oak' (carvalho), 'wood_pine' (pinus), 'wood_maple' (marfim/bordo), 'wood_mahogany' (mogno), 'wood_teak' (teca/escura)\n" +
            "   - Aços e Ferros: 'steel_polished', 'steel_brushed', 'steel_matte', 'steel_cast', 'stainless_satin', 'chrome', 'galvanized', 'iron_cast'\n" +
            "   - Alumínios: 'aluminum_satin', 'aluminum_brushed', 'aluminum_polished', 'aluminum_cast', 'aluminum_anodized_blue'\n" +
            "   - Cobre, Latão e Bronze: 'copper_polished', 'copper_brushed', 'brass_polished', 'bronze_polished'\n" +
            "   - Outros Metais: 'titanium_satin', 'nickel_brushed', 'zinc_matte', 'magnesium_brushed'\n" +
            "   - Polímeros: 'plastic_polished', 'plastic_textured', 'plastic_clear'\n" +
            "   - Elastômeros: 'rubber_matte', 'rubber_glossy', 'rubber_textured'\n" +
            "   - Compósitos e Outros: 'carbon_fiber', 'glass_fiber', 'glass_clear', 'ceramic_porcelain', 'stone_concrete'\n" +
            "   Indique em 'rgb_color' a cor de superfície predominante em valores inteiros (0-255).\n\n" +

            "6. FONTE TÉCNICA PESQUISADA ('property_source'):\n" +
            "   Indique obrigatoriamente a norma técnica, datasheet de fabricante ou compêndio de engenharia consultado (ex: 'ASTM A36 / MatWeb', 'ABNT NBR 15316 / MDF Industrial', 'ISO 527 / Sabic Datasheet', 'DIN EN 10083-3').\n\n" +

            "Retorne EXCLUSIVAMENTE o objeto JSON plano conforme o schema estrito.";

        /// <summary>
        /// Consulta a API do Google Gemini e lista os modelos autorizados na conta do usuário.
        /// </summary>
        public async Task<List<string>> ListAvailableGeminiModelsAsync(string apiKey)
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

                        if (name.Contains("embedding") || name.Contains("imagen") || name.Contains("veo") ||
                            name.Contains("aqa") || name.Contains("tts") || (name.Contains("preview") && name.Contains("image")))
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

            return modelList
                .OrderByDescending(m => m.Contains("3.5-flash-lite"))
                .ThenByDescending(m => m.Contains("3.5-flash"))
                .ThenByDescending(m => m.Contains("3.6-flash"))
                .ThenByDescending(m => m.Contains("flash-lite"))
                .ThenByDescending(m => m.Contains("flash"))
                .ToList();
        }

        /// <summary>
        /// Gera as propriedades completas do material com modelo constitutivo e aparência fotorrealista.
        /// </summary>
        public async Task<MaterialProperties> GenerateMaterialPropertiesAsync(
            string provider,
            string apiKey,
            string model,
            string materialName,
            string additionalContext,
            Action<string> onStatusUpdate = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("A chave de API não foi informada.");

            if (string.IsNullOrWhiteSpace(materialName))
                throw new ArgumentException("O nome do material é obrigatório.");

            string cleanKey = SanitizeApiKey(apiKey);
            string userPrompt = BuildUserPrompt(materialName, additionalContext);

            MaterialProperties props;
            if (string.Equals(provider, "Google Gemini", StringComparison.OrdinalIgnoreCase))
            {
                props = await GenerateWithGeminiFallbackAsync(cleanKey, model, materialName, userPrompt, onStatusUpdate);
            }
            else if (string.Equals(provider, "DeepSeek", StringComparison.OrdinalIgnoreCase))
            {
                props = await GenerateWithOpenAiCompatibleAsync(cleanKey, model, "https://api.deepseek.com/chat/completions", materialName, userPrompt, onStatusUpdate);
            }
            else
            {
                props = await GenerateWithOpenAiCompatibleAsync(cleanKey, model, "https://api.openai.com/v1/chat/completions", materialName, userPrompt, onStatusUpdate);
            }

            ValidateAndEnrichPhysics(props, materialName);
            return props;
        }

        private async Task<MaterialProperties> GenerateWithGeminiFallbackAsync(
            string apiKey,
            string preferredModel,
            string materialName,
            string userPrompt,
            Action<string> onStatusUpdate)
        {
            string cleanModel = CleanModelName(preferredModel);

            var candidateModels = new List<string>();
            if (!string.IsNullOrWhiteSpace(cleanModel)) candidateModels.Add(cleanModel);

            string[] fallbackDefaults = { "gemini-3.5-flash-lite", "gemini-3.5-flash", "gemini-3.6-flash" };
            foreach (var fb in fallbackDefaults)
            {
                if (!candidateModels.Contains(fb, StringComparer.OrdinalIgnoreCase))
                    candidateModels.Add(fb);
            }

            string lastError = string.Empty;

            for (int i = 0; i < candidateModels.Count; i++)
            {
                string currentModel = candidateModels[i];
                onStatusUpdate?.Invoke($"Consultando IA ({currentModel})...");

                // 1ª tentativa: Com responseSchema estrito (garante 100% dos campos tipados na raiz)
                var result = await TryCallGeminiAsync(apiKey, currentModel, materialName, userPrompt, useStrictSchema: true);

                if (result.Success && result.Properties != null && result.Properties.ElasticModulus > 0)
                {
                    return result.Properties;
                }

                // 2ª tentativa no mesmo modelo: Sem responseSchema caso o modelo específico rejeite schema complexo
                if (result.StatusCode == HttpStatusCode.BadRequest &&
                    (result.ErrorMessage == null || !result.ErrorMessage.Contains("API_KEY_INVALID")))
                {
                    onStatusUpdate?.Invoke($"Ajustando schema para {currentModel}...");
                    var retryCompat = await TryCallGeminiAsync(apiKey, currentModel, materialName, userPrompt, useStrictSchema: false);
                    if (retryCompat.Success && retryCompat.Properties != null && retryCompat.Properties.ElasticModulus > 0)
                    {
                        return retryCompat.Properties;
                    }
                    lastError = retryCompat.ErrorMessage;
                }
                else
                {
                    lastError = result.ErrorMessage;
                }

                if (result.StatusCode == HttpStatusCode.Forbidden ||
                    (result.ErrorMessage != null && result.ErrorMessage.Contains("API_KEY_INVALID")))
                {
                    throw new Exception($"Chave de API inválida ou sem permissão: {ExtractErrorMessage(result.ErrorMessage)}");
                }

                if (result.StatusCode == HttpStatusCode.NotFound)
                {
                    string suggested = ExtractSuggestedModelFromError(result.ErrorMessage);
                    if (!string.IsNullOrEmpty(suggested) &&
                        !candidateModels.Contains(suggested, StringComparer.OrdinalIgnoreCase) &&
                        !suggested.Contains("3.8-flash"))
                    {
                        candidateModels.Insert(i + 1, suggested);
                    }
                }
            }

            throw new Exception($"Não foi possível gerar o material na API Gemini.\nDetalhes: {ExtractErrorMessage(lastError)}");
        }

        private class AttemptResult
        {
            public bool Success { get; set; }
            public HttpStatusCode StatusCode { get; set; }
            public string ErrorMessage { get; set; }
            public MaterialProperties Properties { get; set; }
        }

        private async Task<AttemptResult> TryCallGeminiAsync(
            string apiKey,
            string model,
            string materialName,
            string userPrompt,
            bool useStrictSchema)
        {
            string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(apiKey)}";

            var generationConfig = new Dictionary<string, object>
            {
                ["temperature"] = 0.1,
                ["responseMimeType"] = "application/json"
            };

            if (useStrictSchema)
            {
                generationConfig["responseSchema"] = BuildGeminiJsonSchema();
            }

            var payload = new Dictionary<string, object>
            {
                ["systemInstruction"] = new Dictionary<string, object>
                {
                    ["parts"] = new object[]
                    {
                        new Dictionary<string, string> { ["text"] = SystemPrompt }
                    }
                },
                ["contents"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["parts"] = new object[]
                        {
                            new Dictionary<string, string> { ["text"] = userPrompt }
                        }
                    }
                },
                ["generationConfig"] = generationConfig
            };

            string jsonRequest = _serializer.Serialize(payload);

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    request.Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                    using (HttpResponseMessage response = await _httpClient.SendAsync(request).ConfigureAwait(false))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            return new AttemptResult
                            {
                                Success = false,
                                StatusCode = response.StatusCode,
                                ErrorMessage = responseBody
                            };
                        }

                        var root = _serializer.Deserialize<Dictionary<string, object>>(responseBody);
                        var candidates = root?["candidates"] as System.Collections.ArrayList;
                        var firstCandidate = candidates?[0] as Dictionary<string, object>;
                        var contentObj = firstCandidate?["content"] as Dictionary<string, object>;
                        var parts = contentObj?["parts"] as System.Collections.ArrayList;
                        var firstPart = parts?[0] as Dictionary<string, object>;
                        string text = firstPart?["text"] as string;

                        if (string.IsNullOrWhiteSpace(text))
                            throw new Exception("Resposta vazia da API Gemini.");

                        var props = ParseMaterialJson(text, materialName);
                        return new AttemptResult
                        {
                            Success = true,
                            StatusCode = response.StatusCode,
                            Properties = props
                        };
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return new AttemptResult
                {
                    Success = false,
                    StatusCode = HttpStatusCode.RequestTimeout,
                    ErrorMessage = $"Tempo limite esgotado ao conectar ao modelo '{model}'. Tentando modelo alternativo..."
                };
            }
            catch (Exception ex)
            {
                return new AttemptResult
                {
                    Success = false,
                    StatusCode = HttpStatusCode.InternalServerError,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<MaterialProperties> GenerateWithOpenAiCompatibleAsync(
            string apiKey,
            string model,
            string endpointUrl,
            string materialName,
            string userPrompt,
            Action<string> onStatusUpdate)
        {
            string cleanModel = CleanModelName(model);
            onStatusUpdate?.Invoke($"Consultando modelo ({cleanModel})...");

            var payload = new Dictionary<string, object>
            {
                ["model"] = cleanModel,
                ["temperature"] = 0.1,
                ["messages"] = new object[]
                {
                    new Dictionary<string, string> { ["role"] = "system", ["content"] = SystemPrompt },
                    new Dictionary<string, string> { ["role"] = "user", ["content"] = userPrompt }
                }
            };

            if (endpointUrl.Contains("openai.com"))
            {
                payload["response_format"] = new Dictionary<string, object>
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new Dictionary<string, object>
                    {
                        ["name"] = "solidworks_complete_material",
                        ["strict"] = true,
                        ["schema"] = BuildOpenAiJsonSchema()
                    }
                };
            }
            else
            {
                payload["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" };
            }

            string jsonRequest = _serializer.Serialize(payload);

            using (var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                using (HttpResponseMessage response = await _httpClient.SendAsync(request).ConfigureAwait(false))
                {
                    string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception($"Erro na chamada ({response.StatusCode}): {ExtractErrorMessage(responseBody)}");
                    }

                    var root = _serializer.Deserialize<Dictionary<string, object>>(responseBody);
                    var choices = root?["choices"] as System.Collections.ArrayList;
                    var firstChoice = choices?[0] as Dictionary<string, object>;
                    var message = firstChoice?["message"] as Dictionary<string, object>;
                    string content = message?["content"] as string;

                    if (string.IsNullOrWhiteSpace(content))
                        throw new Exception("Conteúdo retornado pela IA está vazio.");

                    return ParseMaterialJson(content, materialName);
                }
            }
        }

        /// <summary>
        /// Interpreta o JSON retornado pela IA tanto por desserialização direta de contrato
        /// quanto por busca recursiva em dicionários (à prova de sub-objetos aninhados).
        /// </summary>
        private MaterialProperties ParseMaterialJson(string rawJson, string materialName)
        {
            string cleaned = CleanMarkdownJson(rawJson);

            // 1. Tenta desserialização de contrato e valida se todas as grandezas principais vieram preenchidas
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(MaterialProperties));
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(cleaned)))
                {
                    var props = serializer.ReadObject(ms) as MaterialProperties;
                    if (props != null && props.ElasticModulus > 0 && props.MassDensity > 0 && props.TensileStrength > 0)
                    {
                        ValidateAndEnrichPhysics(props, materialName);
                        return props;
                    }
                }
            }
            catch { }

            // 2. Fallback: Parser recursivo em árvore de dicionários (acha as chaves mesmo se aninhadas)
            var dict = _serializer.Deserialize<Dictionary<string, object>>(cleaned);
            if (dict == null)
                throw new Exception("Não foi possível interpretar o JSON retornado pela IA.");

            var result = new MaterialProperties
            {
                ElasticModulus = ExtractDoubleRecursive(dict, "elastic_modulus", "EX", "modulo_elasticidade", "modulo_elastico", "young_modulus"),
                PoissonRatio = ExtractDoubleRecursive(dict, "poisson_ratio", "NUXY", "coeficiente_poisson", "poisson"),
                ShearModulus = ExtractDoubleRecursive(dict, "shear_modulus", "GXY", "modulo_cisalhamento", "cisalhamento"),
                MassDensity = ExtractDoubleRecursive(dict, "mass_density", "DENS", "massa_especifica", "densidade", "density"),
                TensileStrength = ExtractDoubleRecursive(dict, "tensile_strength", "SIGXT", "resistencia_tracao", "limite_tracao"),
                CompressiveStrength = ExtractDoubleRecursive(dict, "compressive_strength", "SIGXC", "resistencia_compressao", "limite_compressao"),
                YieldStrength = ExtractDoubleRecursive(dict, "yield_strength", "SIGYLD", "limite_escoamento", "escoamento"),
                ThermalExpansionCoefficient = ExtractDoubleRecursive(dict, "thermal_expansion_coefficient", "ALPX", "expansao_termica", "coeficiente_expansao_termica"),
                ThermalConductivity = ExtractDoubleRecursive(dict, "thermal_conductivity", "KX", "condutividade_termica"),
                SpecificHeat = ExtractDoubleRecursive(dict, "specific_heat", "C", "calor_especifico"),
                MaterialDamping = ExtractDoubleRecursive(dict, "material_damping", "MDAMP", "amortecimento"),
                ModelType = ExtractStringRecursive(dict, "model_type", "tipo_modelo") ?? "Isotrópico linear elástico",
                ClassificationCategory = ExtractStringRecursive(dict, "classification_category", "categoria", "classification") ?? "Materiais Gerados por IA",
                PropertySource = ExtractStringRecursive(dict, "property_source", "origem", "fonte") ?? "Literatura Técnica de Engenharia / IA",
                TechnicalDescription = ExtractStringRecursive(dict, "technical_description", "descricao", "description") ?? $"Material {materialName} gerado via IA",
                FailureCriterion = ExtractStringRecursive(dict, "failure_criterion", "criterio_falha") ?? "Max von Mises Stress",
                XHatch = ExtractStringRecursive(dict, "xhatch", "hachura") ?? "ANSI31 (Iron BrickStone)",
                AppearanceFamily = ExtractStringRecursive(dict, "appearance_family", "familia_aparencia", "shader_family") ?? "steel_polished"
            };

            result.Appearance = ExtractAppearance(dict);
            result.RgbColor = ExtractRgbColor(dict);

            ValidateAndEnrichPhysics(result, materialName);
            return result;
        }

        /// <summary>
        /// Valida fisicamente todas as propriedades retornadas pela IA, corrige escalas (ex: GPa/MPa para Pa)
        /// e calcula grandezas derivadas (como Módulo de Cisalhamento ou Limite Elástico) caso alguma venha zerada.
        /// </summary>
        private static void ValidateAndEnrichPhysics(MaterialProperties props, string materialName)
        {
            if (props == null) return;

            // 1. Coeficiente de Poisson deve estar entre 0.05 e 0.499
            if (props.PoissonRatio <= 0.0 || props.PoissonRatio >= 0.5)
            {
                props.PoissonRatio = 0.30;
            }

            // 2. Corrige escala do Módulo de Elasticidade caso a IA tenha retornado em GPa ou MPa em vez de Pa (N/m²)
            // Qualquer material sólido de engenharia tem E >= 1 MPa (1e6 Pa). Se vier < 1e6 (ex: 205 GPa ou 4000 MPa), converte:
            if (props.ElasticModulus > 0 && props.ElasticModulus < 1000)
            {
                // Provavelmente veio em GPa (ex: 205 para aço ou 4 para MDF)
                props.ElasticModulus *= 1e9;
            }
            else if (props.ElasticModulus >= 1000 && props.ElasticModulus < 1e6)
            {
                // Provavelmente veio em MPa (ex: 4000 MPa)
                props.ElasticModulus *= 1e6;
            }
            else if (props.ElasticModulus <= 0)
            {
                props.ElasticModulus = 2.0e9; // Fallback seguro 2 GPa
            }

            // 3. Módulo de Cisalhamento (G = E / (2 * (1 + v)))
            if (props.ShearModulus > 0 && props.ShearModulus < 1000)
            {
                props.ShearModulus *= 1e9;
            }
            else if (props.ShearModulus >= 1000 && props.ShearModulus < 1e6)
            {
                props.ShearModulus *= 1e6;
            }
            if (props.ShearModulus <= 0)
            {
                props.ShearModulus = Math.Round(props.ElasticModulus / (2.0 * (1.0 + props.PoissonRatio)), 0);
            }

            // 4. Massa Específica (kg/m³):
            // Corrige se veio em g/cm³ (ex: 7.85 ou 0.75 ou 1.05)
            if (props.MassDensity > 0 && props.MassDensity < 25.0)
            {
                props.MassDensity *= 1000.0;
            }
            else if (props.MassDensity <= 0)
            {
                props.MassDensity = 1000.0;
            }

            // Sanidade Física Óbvia de Densidade: Plásticos, borrachas e madeiras não podem ser mais pesados que metais densos (ex: ouro ou aço)
            string lowerName = (materialName ?? "").ToLowerInvariant();
            string lowerCat = (props.ClassificationCategory ?? "").ToLowerInvariant();
            bool isLightMaterial = lowerName.Contains("mdf") || lowerName.Contains("madeira") || lowerName.Contains("wood") ||
                                   lowerName.Contains("plást") || lowerName.Contains("plast") || lowerName.Contains("polim") ||
                                   lowerName.Contains("nylon") || lowerName.Contains("abs") || lowerName.Contains("borracha") ||
                                   lowerName.Contains("rubber") || lowerCat.Contains("madeira") || lowerCat.Contains("plást") || lowerCat.Contains("borracha");

            if (isLightMaterial && props.MassDensity > 3000.0)
            {
                // Se um plástico/madeira veio com densidade acima de 3000 kg/m³ (ex: 7500 ou 19000), normaliza para a faixa típica
                if (props.MassDensity >= 100000.0)
                {
                    props.MassDensity /= 1000.0;
                }
                else
                {
                    props.MassDensity = lowerName.Contains("mdf") ? 750.0 : (lowerName.Contains("borracha") ? 1200.0 : 1050.0);
                }
            }

            // 5. Resistência à Tração (Pa): se veio em MPa (ex: 565 ou 22), multiplica por 10^6
            if (props.TensileStrength > 0 && props.TensileStrength < 1e5)
            {
                props.TensileStrength *= 1e6;
            }
            else if (props.TensileStrength <= 0)
            {
                props.TensileStrength = Math.Round(props.ElasticModulus * 0.005, 0);
            }

            // 6. Resistência à Compressão (Pa)
            if (props.CompressiveStrength > 0 && props.CompressiveStrength < 1e5)
            {
                props.CompressiveStrength *= 1e6;
            }
            else if (props.CompressiveStrength <= 0)
            {
                props.CompressiveStrength = props.TensileStrength;
            }

            // 7. Limite de Escoamento (Pa): NUNCA pode ser 0 e NUNCA pode ser superior à Resistência à Tração
            if (props.YieldStrength > 0 && props.YieldStrength < 1e5)
            {
                props.YieldStrength *= 1e6;
            }
            else if (props.YieldStrength <= 0)
            {
                // Para materiais frágeis/madeiras onde a IA não indicou escoamento plástico nítido, adota 85% da resistência à tração
                props.YieldStrength = Math.Round(props.TensileStrength * 0.85, 0);
            }

            // Regra Óbvia: Escoamento não pode ultrapassar o limite último de tração
            if (props.TensileStrength > 0 && props.YieldStrength > props.TensileStrength)
            {
                props.YieldStrength = Math.Round(props.TensileStrength * 0.85, 0);
            }

            // 8. Coeficiente de Expansão Térmica (1/K)
            if (props.ThermalExpansionCoefficient > 0.01)
            {
                // Se a IA retornou em µm/(m·K) (ex: 11.5 em vez de 11.5e-6)
                props.ThermalExpansionCoefficient *= 1e-6;
            }
            else if (props.ThermalExpansionCoefficient <= 0)
            {
                props.ThermalExpansionCoefficient = 1.2e-5;
            }

            // 9. Condutividade Térmica (W/(m·K)) e Calor Específico (J/(kg·K))
            if (props.ThermalConductivity <= 0)
            {
                props.ThermalConductivity = props.MassDensity > 2000 ? 45.0 : 0.20;
            }
            if (props.SpecificHeat <= 0)
            {
                props.SpecificHeat = props.MassDensity > 2000 ? 486.0 : 1400.0;
            }
            if (props.MaterialDamping <= 0 || props.MaterialDamping > 1.0)
            {
                props.MaterialDamping = 0.02;
            }

            // 10. Sanitização de Cor RGB e Aparência
            if (props.RgbColor == null)
            {
                props.RgbColor = new RgbColor { R = 180, G = 182, B = 185 };
            }
            else
            {
                props.RgbColor.R = Math.Max(0, Math.Min(255, props.RgbColor.R));
                props.RgbColor.G = Math.Max(0, Math.Min(255, props.RgbColor.G));
                props.RgbColor.B = Math.Max(0, Math.Min(255, props.RgbColor.B));
            }

            if (props.Appearance == null)
            {
                props.Appearance = new MaterialAppearance
                {
                    Specularity = 0.5,
                    Shininess = 0.3,
                    Diffuse = 0.8,
                    Ambient = 0.5,
                    Transparency = 0.0
                };
            }
        }

        private static double ExtractDoubleRecursive(Dictionary<string, object> dict, params string[] possibleKeys)
        {
            if (dict == null) return 0.0;

            // 1. Procura no nível atual
            foreach (var key in possibleKeys)
            {
                foreach (var kvp in dict)
                {
                    if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        double val = ConvertToDouble(kvp.Value);
                        if (val != 0.0) return val;
                    }
                }
            }

            // 2. Procura recursivamente em sub-dicionários
            foreach (var kvp in dict)
            {
                if (kvp.Value is Dictionary<string, object> subDict)
                {
                    double subVal = ExtractDoubleRecursive(subDict, possibleKeys);
                    if (subVal != 0.0) return subVal;
                }
            }

            return 0.0;
        }

        private static string ExtractStringRecursive(Dictionary<string, object> dict, params string[] possibleKeys)
        {
            if (dict == null) return null;

            foreach (var key in possibleKeys)
            {
                foreach (var kvp in dict)
                {
                    if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase) && kvp.Value is string s && !string.IsNullOrWhiteSpace(s))
                    {
                        return s.Trim();
                    }
                }
            }

            foreach (var kvp in dict)
            {
                if (kvp.Value is Dictionary<string, object> subDict)
                {
                    string subStr = ExtractStringRecursive(subDict, possibleKeys);
                    if (!string.IsNullOrWhiteSpace(subStr)) return subStr;
                }
            }

            return null;
        }

        private static MaterialAppearance ExtractAppearance(Dictionary<string, object> dict)
        {
            var app = new MaterialAppearance();

            foreach (var kvp in dict)
            {
                if (string.Equals(kvp.Key, "appearance", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "aparencia", StringComparison.OrdinalIgnoreCase))
                {
                    if (kvp.Value is Dictionary<string, object> aDict)
                    {
                        app.TexturePath = ExtractStringRecursive(aDict, "texture_path", "caminho_textura");
                        app.ShaderPath = ExtractStringRecursive(aDict, "shader_path", "caminho_shader");
                        app.ShaderName = ExtractStringRecursive(aDict, "shader_name", "nome_shader");
                        app.Specularity = ExtractDoubleRecursive(aDict, "specularity", "especularidade");
                        app.Shininess = ExtractDoubleRecursive(aDict, "shininess", "nitidez", "polimento");
                        app.Diffuse = ExtractDoubleRecursive(aDict, "diffuse", "difusao");
                        app.Ambient = ExtractDoubleRecursive(aDict, "ambient", "ambiente");
                        app.Transparency = ExtractDoubleRecursive(aDict, "transparency", "transparencia");
                        return app;
                    }
                }
            }

            app.Specularity = 0.6;
            app.Shininess = 0.3;
            app.Diffuse = 0.8;
            app.Ambient = 0.5;
            return app;
        }

        private static RgbColor ExtractRgbColor(Dictionary<string, object> dict)
        {
            foreach (var kvp in dict)
            {
                if (string.Equals(kvp.Key, "rgb_color", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "cor_rgb", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "rgb", StringComparison.OrdinalIgnoreCase))
                {
                    if (kvp.Value is Dictionary<string, object> cDict)
                    {
                        int r = (int)ConvertToDouble(cDict.ContainsKey("r") ? cDict["r"] : (cDict.ContainsKey("R") ? cDict["R"] : 180));
                        int g = (int)ConvertToDouble(cDict.ContainsKey("g") ? cDict["g"] : (cDict.ContainsKey("G") ? cDict["G"] : 182));
                        int b = (int)ConvertToDouble(cDict.ContainsKey("b") ? cDict["b"] : (cDict.ContainsKey("B") ? cDict["B"] : 185));
                        return new RgbColor { R = r, G = g, B = b };
                    }
                }
            }
            return new RgbColor { R = 180, G = 182, B = 185 };
        }

        private static double ConvertToDouble(object rawVal)
        {
            if (rawVal == null) return 0.0;
            if (rawVal is double d) return d;
            if (rawVal is int i) return (double)i;
            if (rawVal is long l) return (double)l;
            if (rawVal is float f) return (double)f;
            if (rawVal is decimal dec) return (double)dec;

            string s = rawVal.ToString().Trim();
            if (string.IsNullOrEmpty(s)) return 0.0;

            // Suporta notação científica (ex: 2.05e11 ou 4.0E+9)
            s = s.Replace(",", ".");
            var match = Regex.Match(s, @"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?");
            if (match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                return parsed;
            }

            return 0.0;
        }

        private static Dictionary<string, object> BuildGeminiJsonSchema()
        {
            return new Dictionary<string, object>
            {
                ["type"] = "OBJECT",
                ["properties"] = new Dictionary<string, object>
                {
                    ["elastic_modulus"] = new Dictionary<string, object> { ["type"] = "NUMBER", ["description"] = "Módulo elástico (EX) em N/m² (Pa). Ex: Aço=205000000000, MDF=4000000000" },
                    ["poisson_ratio"] = new Dictionary<string, object> { ["type"] = "NUMBER", ["description"] = "Coeficiente de Poisson (NUXY) adimensional (0.1 a 0.49)" },
                    ["shear_modulus"] = new Dictionary<string, object> { ["type"] = "NUMBER", ["description"] = "Módulo de cisalhamento (GXY) em N/m² (Pa). Ex: Aço=80000000000, MDF=1500000000" },
                    ["mass_density"] = new Dictionary<string, object> { ["type"] = "NUMBER", ["description"] = "Massa específica (DENS) em kg/m³. Ex: Aço=7850, MDF=750" },
                    ["tensile_strength"] = new Dictionary<string, object> { ["type"] = "NUMBER", ["description"] = "Resistência à tração (SIGXT) em N/m² (Pa). Ex: Aço=565000000, MDF=22000000" },
                    ["compressive_strength"] = new Dictionary<string, object> { ["type"] = "NUMBER", ["description"] = "Resistência à compressão (SIGXC) em N/m² (Pa). Ex: Aço=565000000, MDF=32000000" },
                    ["yield_strength"] = new Dictionary<string, object> { ["type"] = "NUMBER", ["description"] = "Limite de escoamento ou limite proporcional elástico (SIGYLD) em N/m² (Pa) (> 0). Ex: Aço=310000000, MDF=18000000" },
                    ["thermal_expansion_coefficient"] = new Dictionary<string, object> { ["type"] = "NUMBER", ["description"] = "Coeficiente de expansão térmica (ALPX) em 1/K. Ex: 0.000012" },
                    ["thermal_conductivity"] = new Dictionary<string, object> { ["type"] = "NUMBER", ["description"] = "Condutividade térmica (KX) em W/(m·K). Ex: Aço=49.8, MDF=0.14" },
                    ["specific_heat"] = new Dictionary<string, object> { ["type"] = "NUMBER", ["description"] = "Calor específico (C) em J/(kg·K). Ex: Aço=486, MDF=1700" },
                    ["material_damping"] = new Dictionary<string, object> { ["type"] = "NUMBER", ["description"] = "Coeficiente de amortecimento (MDAMP) adimensional. Ex: 0.02" },
                    ["model_type"] = new Dictionary<string, object> { ["type"] = "STRING", ["description"] = "Tipo de modelo constitutivo do SolidWorks Simulation" },
                    ["classification_category"] = new Dictionary<string, object> { ["type"] = "STRING", ["description"] = "Categoria do material em português (ex: Madeiras, Aços, Ligas de Alumínio, Plásticos, Borrachas)" },
                    ["property_source"] = new Dictionary<string, object> { ["type"] = "STRING", ["description"] = "Norma ou fonte técnica de referência" },
                    ["technical_description"] = new Dictionary<string, object> { ["type"] = "STRING", ["description"] = "Resumo técnico do material e estado de fornecimento" },
                    ["failure_criterion"] = new Dictionary<string, object> { ["type"] = "STRING", ["description"] = "Critério de falha (Max von Mises Stress, Mohr-Coulomb Stress, Max Normal Stress)" },
                    ["xhatch"] = new Dictionary<string, object> { ["type"] = "STRING", ["description"] = "Padrão de hachura 2D do SolidWorks" },
                    ["appearance_family"] = new Dictionary<string, object>
                    {
                        ["type"] = "STRING",
                        ["description"] = "Identificador da família visual: wood_beech, wood_pine, wood_maple, wood_oak, wood_teak, wood_mahogany, steel_polished, steel_brushed, steel_matte, steel_cast, stainless_satin, chrome, galvanized, iron_cast, aluminum_satin, aluminum_brushed, aluminum_polished, aluminum_cast, copper_polished, brass_polished, bronze_polished, titanium_satin, nickel_brushed, zinc_matte, plastic_polished, plastic_textured, plastic_clear, rubber_glossy, rubber_matte, carbon_fiber, glass_fiber, glass_clear, ceramic_porcelain, stone_concrete"
                    },
                    ["appearance"] = new Dictionary<string, object>
                    {
                        ["type"] = "OBJECT",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["specularity"] = new Dictionary<string, object> { ["type"] = "NUMBER" },
                            ["shininess"] = new Dictionary<string, object> { ["type"] = "NUMBER" },
                            ["diffuse"] = new Dictionary<string, object> { ["type"] = "NUMBER" },
                            ["ambient"] = new Dictionary<string, object> { ["type"] = "NUMBER" },
                            ["transparency"] = new Dictionary<string, object> { ["type"] = "NUMBER" }
                        },
                        ["required"] = new[] { "specularity", "shininess", "diffuse", "ambient", "transparency" }
                    },
                    ["rgb_color"] = new Dictionary<string, object>
                    {
                        ["type"] = "OBJECT",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["r"] = new Dictionary<string, object> { ["type"] = "INTEGER" },
                            ["g"] = new Dictionary<string, object> { ["type"] = "INTEGER" },
                            ["b"] = new Dictionary<string, object> { ["type"] = "INTEGER" }
                        },
                        ["required"] = new[] { "r", "g", "b" }
                    }
                },
                ["required"] = new[]
                {
                    "elastic_modulus", "poisson_ratio", "shear_modulus", "mass_density",
                    "tensile_strength", "compressive_strength", "yield_strength",
                    "thermal_expansion_coefficient", "thermal_conductivity", "specific_heat",
                    "material_damping", "model_type", "classification_category",
                    "property_source", "technical_description", "failure_criterion",
                    "xhatch", "appearance_family", "appearance", "rgb_color"
                }
            };
        }

        private static Dictionary<string, object> BuildOpenAiJsonSchema()
        {
            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new Dictionary<string, object>
                {
                    ["elastic_modulus"] = new Dictionary<string, object> { ["type"] = "number" },
                    ["poisson_ratio"] = new Dictionary<string, object> { ["type"] = "number" },
                    ["shear_modulus"] = new Dictionary<string, object> { ["type"] = "number" },
                    ["mass_density"] = new Dictionary<string, object> { ["type"] = "number" },
                    ["tensile_strength"] = new Dictionary<string, object> { ["type"] = "number" },
                    ["compressive_strength"] = new Dictionary<string, object> { ["type"] = "number" },
                    ["yield_strength"] = new Dictionary<string, object> { ["type"] = "number" },
                    ["thermal_expansion_coefficient"] = new Dictionary<string, object> { ["type"] = "number" },
                    ["thermal_conductivity"] = new Dictionary<string, object> { ["type"] = "number" },
                    ["specific_heat"] = new Dictionary<string, object> { ["type"] = "number" },
                    ["material_damping"] = new Dictionary<string, object> { ["type"] = "number" },
                    ["model_type"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["classification_category"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["property_source"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["technical_description"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["failure_criterion"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["xhatch"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["appearance_family"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["appearance"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["specularity"] = new Dictionary<string, object> { ["type"] = "number" },
                            ["shininess"] = new Dictionary<string, object> { ["type"] = "number" },
                            ["diffuse"] = new Dictionary<string, object> { ["type"] = "number" },
                            ["ambient"] = new Dictionary<string, object> { ["type"] = "number" },
                            ["transparency"] = new Dictionary<string, object> { ["type"] = "number" }
                        },
                        ["required"] = new[] { "specularity", "shininess", "diffuse", "ambient", "transparency" }
                    },
                    ["rgb_color"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["r"] = new Dictionary<string, object> { ["type"] = "integer" },
                            ["g"] = new Dictionary<string, object> { ["type"] = "integer" },
                            ["b"] = new Dictionary<string, object> { ["type"] = "integer" }
                        },
                        ["required"] = new[] { "r", "g", "b" }
                    }
                },
                ["required"] = new[]
                {
                    "elastic_modulus", "poisson_ratio", "shear_modulus", "mass_density",
                    "tensile_strength", "compressive_strength", "yield_strength",
                    "thermal_expansion_coefficient", "thermal_conductivity", "specific_heat",
                    "material_damping", "model_type", "classification_category",
                    "property_source", "technical_description", "failure_criterion",
                    "xhatch", "appearance_family", "appearance", "rgb_color"
                }
            };
        }

        private static string BuildUserPrompt(string materialName, string additionalContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Pesquise e determine as especificações e propriedades de engenharia para o seguinte material:");
            sb.AppendLine($"• Material: {materialName}");
            if (!string.IsNullOrWhiteSpace(additionalContext))
            {
                sb.AppendLine($"• Detalhes / Aplicação / Fornecedor / Norma informada: {additionalContext}");
            }
            sb.AppendLine();
            sb.AppendLine("Diretrizes de Pesquisa:");
            sb.AppendLine("1. Investigue a literatura técnica, normas (ASTM, ISO, DIN, ABNT, SAE), bancos de dados (MatWeb, ASM) ou catálogos do fabricante para obter os dados experimentais/tabelados reais deste material.");
            sb.AppendLine("2. Respeite as regras de sanidade física e coerência óbvia de engenharia (ex: a densidade deve ser estritamente condizente com a classe do material; o limite de escoamento não pode ser superior à resistência à tração; o coeficiente de Poisson deve ser admissível).");
            sb.AppendLine("3. Converta todas as grandezas para as unidades do Sistema Internacional (SI) exigidas pelo SolidWorks (N/m², kg/m³, W/(m·K), J/(kg·K), 1/K).");
            sb.AppendLine("4. Nenhuma propriedade numérica pode ser 0 ou negativa.");
            sb.AppendLine("5. Indique no campo 'property_source' a norma ou referência técnica encontrada.");
            return sb.ToString();
        }

        private static string SanitizeApiKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            return key.Trim().Replace(" ", "").Replace("\r", "").Replace("\n", "");
        }

        private static string CleanModelName(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return "gemini-3.5-flash-lite";
            string m = model.Trim();
            int spaceIdx = m.IndexOf(' ');
            if (spaceIdx > 0) m = m.Substring(0, spaceIdx);
            if (m.StartsWith("models/")) m = m.Substring(7);
            return m.Trim();
        }

        private static string ExtractSuggestedModelFromError(string errorJson)
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

        private static string CleanMarkdownJson(string text)
        {
            text = text.Trim();
            if (text.StartsWith("```json")) text = text.Substring(7);
            else if (text.StartsWith("```")) text = text.Substring(3);
            if (text.EndsWith("```")) text = text.Substring(0, text.Length - 3);
            return text.Trim();
        }
    }
}
