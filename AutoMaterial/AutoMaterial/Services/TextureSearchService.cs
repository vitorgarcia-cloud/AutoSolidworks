using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AutoMaterial.Services
{
    public class TextureCandidate
    {
        public string Title { get; set; }
        public string ThumbnailUrl { get; set; }
        public string FullImageUrl { get; set; }
        public string SourceName { get; set; }
        public bool IsLocalFile { get; set; }
    }

    /// <summary>
    /// Serviço especializado em buscar imagens de texturas fotorrealistas para materiais de engenharia
    /// via APIs públicas (ambientCG PBR, Wikimedia Commons) e biblioteca local do SolidWorks.
    /// </summary>
    public class TextureSearchService
    {
        private static readonly HttpClient _httpClient;
        private readonly JavaScriptSerializer _serializer;

        static TextureSearchService()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "AutoMaterialSolidWorks/1.0 (engineering-tools; contact@solidworks.com)");
        }

        public TextureSearchService()
        {
            _serializer = new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue
            };
        }

        /// <summary>
        /// Busca até 3 imagens de textura na internet e no repositório de texturas para o material indicado.
        /// </summary>
        public async Task<List<TextureCandidate>> SearchTexturesAsync(string materialName, string appearanceFamily, string classification)
        {
            var results = new List<TextureCandidate>();

            // 1. Gera termos de busca em inglês e português (ex: "mdf wood", "steel", "plastic")
            string[] searchKeywords = BuildSearchKeywords(materialName, appearanceFamily, classification);

            // 2. Tenta buscar no ambientCG (banco aberto de texturas PBR CC0 de alta qualidade)
            foreach (var kw in searchKeywords)
            {
                if (results.Count >= 3) break;
                try
                {
                    var acgResults = await SearchAmbientCgAsync(kw);
                    foreach (var item in acgResults)
                    {
                        if (results.Count >= 3) break;
                        if (!results.Exists(r => r.Title == item.Title || r.ThumbnailUrl == item.ThumbnailUrl))
                        {
                            results.Add(item);
                        }
                    }
                }
                catch { }
            }

            // 3. Tenta buscar no Wikimedia Commons caso ainda falte opções
            if (results.Count < 3)
            {
                foreach (var kw in searchKeywords)
                {
                    if (results.Count >= 3) break;
                    try
                    {
                        var wikiResults = await SearchWikimediaAsync(kw + " texture");
                        foreach (var item in wikiResults)
                        {
                            if (results.Count >= 3) break;
                            if (!results.Exists(r => r.Title == item.Title || r.ThumbnailUrl == item.ThumbnailUrl))
                            {
                                results.Add(item);
                            }
                        }
                    }
                    catch { }
                }
            }

            // 4. Complementa com texturas nativas instaladas no SolidWorks se a internet falhar ou retornar menos de 3
            if (results.Count < 3)
            {
                var localFallbacks = GetLocalSolidWorksTextures(appearanceFamily, classification);
                foreach (var local in localFallbacks)
                {
                    if (results.Count >= 3) break;
                    if (!results.Exists(r => r.FullImageUrl == local.FullImageUrl))
                    {
                        results.Add(local);
                    }
                }
            }

            return results;
        }

        public const string SolidWorksMaterialsIaPath = @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\data\Images\materials-ia";

        /// <summary>
        /// Obtém o diretório de destino prioritário para texturas:
        /// Tenta usar 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\data\Images\materials-ia'
        /// e, caso haja restrição de permissão do Windows, faz fallback automático para 'Documents\AutoMaterial\Textures'.
        /// </summary>
        public static string ResolveMaterialsIaDirectory()
        {
            try
            {
                if (!Directory.Exists(SolidWorksMaterialsIaPath))
                {
                    Directory.CreateDirectory(SolidWorksMaterialsIaPath);
                }

                // Testa se o processo atual possui permissão de escrita no diretório do Program Files
                string testFile = Path.Combine(SolidWorksMaterialsIaPath, ".write_test.tmp");
                File.WriteAllText(testFile, "test");
                if (File.Exists(testFile))
                {
                    File.Delete(testFile);
                }

                return SolidWorksMaterialsIaPath;
            }
            catch
            {
                // Fallback seguro caso o Windows exija privilégios elevados de Administrador
                string documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string fallbackDir = Path.Combine(documentsFolder, "AutoMaterial", "Textures");
                if (!Directory.Exists(fallbackDir))
                {
                    Directory.CreateDirectory(fallbackDir);
                }
                return fallbackDir;
            }
        }

        /// <summary>
        /// Salva uma imagem (seja baixada da internet ou anexada pelo usuário) no diretório 'materials-ia'
        /// e retorna o caminho absoluto pronto para ser associado ao material.
        /// </summary>
        public async Task<string> DownloadAndPrepareTextureAsync(TextureCandidate candidate, string materialName)
        {
            if (candidate == null) return null;

            string texturesDir = ResolveMaterialsIaDirectory();
            string safeName = Regex.Replace(materialName ?? "Material", @"[\\/:*?""<>|]", "_").Trim();
            string extension = ".jpg";

            // Se for arquivo local (ex: anexado pelo usuário ou da biblioteca local)
            if (candidate.IsLocalFile && File.Exists(candidate.FullImageUrl))
            {
                extension = Path.GetExtension(candidate.FullImageUrl);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".jpg";

                string localTarget = Path.Combine(texturesDir, $"{safeName}_texture{extension}");
                try
                {
                    File.Copy(candidate.FullImageUrl, localTarget, true);
                    return localTarget;
                }
                catch
                {
                    // Se falhou no diretório principal por permissão, salva no fallback de Documentos
                    string fallbackTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutoMaterial", "Textures", $"{safeName}_texture{extension}");
                    File.Copy(candidate.FullImageUrl, fallbackTarget, true);
                    return fallbackTarget;
                }
            }

            // Se for imagem remota da internet (URL)
            string targetPath = Path.Combine(texturesDir, $"{safeName}_texture{extension}");
            string downloadUrl = !string.IsNullOrWhiteSpace(candidate.FullImageUrl) ? candidate.FullImageUrl : candidate.ThumbnailUrl;

            try
            {
                using (var response = await _httpClient.GetAsync(downloadUrl))
                {
                    response.EnsureSuccessStatusCode();
                    byte[] data = await response.Content.ReadAsByteArrayAsync();
                    File.WriteAllBytes(targetPath, data);
                }
                return targetPath;
            }
            catch
            {
                // Fallback de download em Documents
                string fallbackTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutoMaterial", "Textures", $"{safeName}_texture{extension}");
                using (var response = await _httpClient.GetAsync(downloadUrl))
                {
                    response.EnsureSuccessStatusCode();
                    byte[] data = await response.Content.ReadAsByteArrayAsync();
                    File.WriteAllBytes(fallbackTarget, data);
                }
                return fallbackTarget;
            }
        }

        /// <summary>
        /// Copia uma imagem local anexada pelo usuário diretamente para a pasta 'materials-ia' do SolidWorks
        /// e também para a pasta mestre de aparências 'C:\SW_Master\Materiais\Aparencias'.
        /// </summary>
        public static string CopyCustomImageToMaterialsIa(string sourceFilePath, string materialName)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                return sourceFilePath;

            string safeName = Regex.Replace(materialName ?? "Material", @"[\\/:*?""<>|]", "_").Trim();
            string extension = Path.GetExtension(sourceFilePath);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".jpg";

            // 1. Salva também uma cópia no diretório mestre C:\SW_Master\Materiais\Aparencias
            try
            {
                string swMasterAparenciasDir = @"C:\SW_Master\Materiais\Aparencias";
                if (!Directory.Exists(swMasterAparenciasDir))
                {
                    Directory.CreateDirectory(swMasterAparenciasDir);
                }
                string masterTargetPath = Path.Combine(swMasterAparenciasDir, $"{safeName}_texture{extension}");
                if (!string.Equals(Path.GetFullPath(sourceFilePath), Path.GetFullPath(masterTargetPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourceFilePath, masterTargetPath, true);
                }
            }
            catch { }

            // 2. Salva na pasta oficial do SolidWorks 'materials-ia'
            string texturesDir = ResolveMaterialsIaDirectory();
            string targetPath = Path.Combine(texturesDir, $"{safeName}_texture{extension}");
            try
            {
                File.Copy(sourceFilePath, targetPath, true);
                return targetPath;
            }
            catch
            {
                string fallbackTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutoMaterial", "Textures", $"{safeName}_texture{extension}");
                File.Copy(sourceFilePath, fallbackTarget, true);
                return fallbackTarget;
            }
        }

        private async Task<List<TextureCandidate>> SearchAmbientCgAsync(string query)
        {
            var list = new List<TextureCandidate>();
            string url = $"https://ambientcg.com/api/v2/full_json?q={Uri.EscapeDataString(query)}&limit=3&type=Material";

            string json = await _httpClient.GetStringAsync(url);
            var root = _serializer.Deserialize<Dictionary<string, object>>(json);

            if (root != null && root.ContainsKey("foundAssets") && root["foundAssets"] is System.Collections.ArrayList assets)
            {
                foreach (var item in assets)
                {
                    if (item is Dictionary<string, object> assetDict)
                    {
                        string displayName = assetDict.ContainsKey("displayName") ? assetDict["displayName"]?.ToString() : "Textura PBR";
                        string assetId = assetDict.ContainsKey("assetId") ? assetDict["assetId"]?.ToString() : "";

                        string thumbUrl = null;
                        if (assetDict.ContainsKey("previewImage") && assetDict["previewImage"] is Dictionary<string, object> previewDict)
                        {
                            if (previewDict.ContainsKey("512-JPG-FFFFFF"))
                                thumbUrl = previewDict["512-JPG-FFFFFF"]?.ToString();
                            else if (previewDict.ContainsKey("256-JPG-FFFFFF"))
                                thumbUrl = previewDict["256-JPG-FFFFFF"]?.ToString();
                        }

                        if (!string.IsNullOrWhiteSpace(thumbUrl))
                        {
                            list.Add(new TextureCandidate
                            {
                                Title = displayName,
                                ThumbnailUrl = thumbUrl,
                                FullImageUrl = thumbUrl,
                                SourceName = "ambientCG (CC0 PBR)",
                                IsLocalFile = false
                            });
                        }
                    }
                }
            }

            return list;
        }

        private async Task<List<TextureCandidate>> SearchWikimediaAsync(string query)
        {
            var list = new List<TextureCandidate>();
            string url = $"https://commons.wikimedia.org/w/api.php?action=query&format=json&formatversion=2&generator=search&gsrnamespace=6&gsrlimit=3&gsrsearch={Uri.EscapeDataString(query)}&prop=imageinfo&iiprop=url|mime&iiurlwidth=400";

            string json = await _httpClient.GetStringAsync(url);
            var root = _serializer.Deserialize<Dictionary<string, object>>(json);

            if (root != null && root.ContainsKey("query") && root["query"] is Dictionary<string, object> queryObj)
            {
                if (queryObj.ContainsKey("pages") && queryObj["pages"] is System.Collections.ArrayList pages)
                {
                    foreach (var p in pages)
                    {
                        if (p is Dictionary<string, object> pageDict && pageDict.ContainsKey("imageinfo") && pageDict["imageinfo"] is System.Collections.ArrayList infoList)
                        {
                            if (infoList.Count > 0 && infoList[0] is Dictionary<string, object> firstInfo)
                            {
                                string mime = firstInfo.ContainsKey("mime") ? firstInfo["mime"]?.ToString() : "";
                                if (mime == "image/jpeg" || mime == "image/png")
                                {
                                    string title = pageDict.ContainsKey("title") ? pageDict["title"]?.ToString()?.Replace("File:", "") : "Textura Web";
                                    string thumbUrl = firstInfo.ContainsKey("thumburl") ? firstInfo["thumburl"]?.ToString() : null;
                                    string fullUrl = firstInfo.ContainsKey("url") ? firstInfo["url"]?.ToString() : thumbUrl;

                                    if (!string.IsNullOrWhiteSpace(thumbUrl))
                                    {
                                        list.Add(new TextureCandidate
                                        {
                                            Title = title,
                                            ThumbnailUrl = thumbUrl,
                                            FullImageUrl = fullUrl,
                                            SourceName = "Wikimedia Commons",
                                            IsLocalFile = false
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return list;
        }

        private List<TextureCandidate> GetLocalSolidWorksTextures(string appearanceFamily, string classification)
        {
            var list = new List<TextureCandidate>();
            string swTexturesPath = @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\data\Images\textures";
            if (!Directory.Exists(swTexturesPath)) return list;

            string targetSubfolder = "wood";
            string family = (appearanceFamily ?? "").ToLowerInvariant();
            string cat = (classification ?? "").ToLowerInvariant();

            if (family.Contains("steel") || family.Contains("iron") || cat.Contains("aço") || cat.Contains("ferro"))
                targetSubfolder = @"metal\brushed";
            else if (family.Contains("aluminum") || cat.Contains("alumínio"))
                targetSubfolder = @"metal\brushed";
            else if (family.Contains("plastic") || cat.Contains("plástic"))
                targetSubfolder = "plastic";
            else if (family.Contains("rubber") || cat.Contains("borracha"))
                targetSubfolder = "rubber";
            else if (family.Contains("carbon") || cat.Contains("compósit"))
                targetSubfolder = "fibers";
            else if (family.Contains("concrete") || family.Contains("stone") || cat.Contains("pedra"))
                targetSubfolder = "stone";

            string fullSearchDir = Path.Combine(swTexturesPath, targetSubfolder);
            if (!Directory.Exists(fullSearchDir)) fullSearchDir = swTexturesPath;

            try
            {
                var files = Directory.GetFiles(fullSearchDir, "*.jpg", SearchOption.AllDirectories);
                int count = 0;
                foreach (var f in files)
                {
                    if (count >= 3) break;
                    string name = Path.GetFileNameWithoutExtension(f);
                    list.Add(new TextureCandidate
                    {
                        Title = $"SolidWorks ({name})",
                        ThumbnailUrl = f,
                        FullImageUrl = f,
                        SourceName = "SolidWorks Library",
                        IsLocalFile = true
                    });
                    count++;
                }
            }
            catch { }

            return list;
        }

        private static string[] BuildSearchKeywords(string materialName, string appearanceFamily, string classification)
        {
            string name = (materialName ?? "").ToLowerInvariant();

            if (name.Contains("mdf") || name.Contains("mdp") || name.Contains("compensado"))
                return new[] { "wood", "plywood", "particle board" };

            if (name.Contains("madeira") || name.Contains("oak") || name.Contains("carvalho") || name.Contains("pinus"))
                return new[] { "wood", "wood grain" };

            if (name.Contains("aço") || name.Contains("steel") || name.Contains("inox") || name.Contains("ferro"))
                return new[] { "steel", "brushed metal", "metal" };

            if (name.Contains("alumínio") || name.Contains("aluminio") || name.Contains("aluminum"))
                return new[] { "aluminum", "brushed metal", "metal" };

            if (name.Contains("cobre") || name.Contains("copper") || name.Contains("latão") || name.Contains("brass") || name.Contains("bronze"))
                return new[] { "copper", "brass", "bronze" };

            if (name.Contains("plástic") || name.Contains("plastic") || name.Contains("nylon") || name.Contains("abs") || name.Contains("peek") || name.Contains("polim"))
                return new[] { "plastic", "polymer" };

            if (name.Contains("borracha") || name.Contains("rubber") || name.Contains("silicone") || name.Contains("elastôm"))
                return new[] { "rubber", "silicone" };

            if (name.Contains("carbon") || name.Contains("fibra"))
                return new[] { "carbon fiber", "fabric" };

            if (name.Contains("concreto") || name.Contains("concrete") || name.Contains("pedra") || name.Contains("stone"))
                return new[] { "concrete", "stone" };

            return new[] { "material", "texture", "metal" };
        }
    }
}
