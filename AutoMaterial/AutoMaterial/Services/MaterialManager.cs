using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Xml;
using AutoMaterial.Models;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AutoMaterial.Services
{
    /// <summary>
    /// Gerencia a criação e atualização incremental do banco de materiais customizado (.sldmat)
    /// como Biblioteca Personalizada Editável (ícone azul no SolidWorks), mapeando:
    /// - Propriedades físicas completas no SI (isotrópicas e ortotrópicas na ordem exata do sldmaterials.xsd)
    /// - Tipo de modelo constitutivo oficial (swsMaterialModelType_e)
    /// - Shaders PhotoView 360 (.p2m), RealView (cgshader2) e Texturas de Imagem (.jpg) oficiais do SolidWorks.
    /// </summary>
    public class MaterialManager
    {
        private readonly ISldWorks _swApp;

        private const string SldMatNamespace = "http://www.solidworks.com/sldmaterials";
        private const string ColorSwatchNamespace = "http://www.solidworks.com/sldcolorswatch";

        private class ShaderPreset
        {
            public string PwShader2Name { get; set; }
            public string PwShader2Path { get; set; }
            public string CgShader2Name { get; set; }
            public string PwShaderName { get; set; }
            public string CgShaderName { get; set; }
            public string SwTexturePath { get; set; }
            public string DefaultXHatch { get; set; }
        }

        public MaterialManager(ISldWorks swApp)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
        }

        public string CreateAndApplyMaterial(
            string materialName,
            string additionalContext,
            MaterialProperties properties,
            MaterialAppearanceSelection appearanceSelection = null)
        {
            if (string.IsNullOrWhiteSpace(materialName))
                throw new ArgumentException("O nome do material não pode ser vazio.", nameof(materialName));

            if (properties == null)
                throw new ArgumentNullException(nameof(properties));

            string documentsFolder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
            string autoMaterialDir = Path.Combine(documentsFolder, "AutoMaterial");
            if (!Directory.Exists(autoMaterialDir))
            {
                Directory.CreateDirectory(autoMaterialDir);
            }

            string sldMatPath = Path.Combine(autoMaterialDir, "AutoMaterial.sldmat");
            string cleanMaterialName = materialName.Trim();

            // 1. Configuração dos diretórios locais e mestres (C:\SW_Master\Materiais)
            string swMasterDir = @"C:\SW_Master\Materiais";
            string swMasterMateriaisDir = Path.Combine(swMasterDir, "Materiais");
            string swMasterAparenciasDir = Path.Combine(swMasterDir, "Aparencias");

            try
            {
                if (!Directory.Exists(swMasterMateriaisDir)) Directory.CreateDirectory(swMasterMateriaisDir);
                if (!Directory.Exists(swMasterAparenciasDir)) Directory.CreateDirectory(swMasterAparenciasDir);
            }
            catch { }

            // Garante registro de todos os caminhos nas Opções do SolidWorks -> Bancos de Dados de Materiais e Texturas/Aparências
            EnsureMaterialDatabaseFolderRegistered(autoMaterialDir);
            EnsureMaterialDatabaseFolderRegistered(swMasterMateriaisDir);
            EnsureTextureFolderRegistered(TextureSearchService.ResolveMaterialsIaDirectory());
            EnsureTextureFolderRegistered(swMasterAparenciasDir);

            // 2. Cria ou atualiza incrementalmente o arquivo AutoMaterial.sldmat na biblioteca padrão do Add-In
            SaveOrUpdateMaterialInLibrary(sldMatPath, cleanMaterialName, additionalContext, properties, appearanceSelection);

            // 3. Salva também uma cópia no diretório mestre C:\SW_Master\Materiais\Materiais
            if (Directory.Exists(swMasterMateriaisDir))
            {
                try
                {
                    string masterSldMat = Path.Combine(swMasterMateriaisDir, "AutoMaterial.sldmat");
                    SaveOrUpdateMaterialInLibrary(masterSldMat, cleanMaterialName, additionalContext, properties, appearanceSelection);
                }
                catch { }
            }

            // 4. Se houver textura associada, garante que uma cópia exista em C:\SW_Master\Materiais\Aparencias
            if (Directory.Exists(swMasterAparenciasDir) && !string.IsNullOrWhiteSpace(properties.Appearance?.TexturePath) && File.Exists(properties.Appearance.TexturePath))
            {
                try
                {
                    string ext = Path.GetExtension(properties.Appearance.TexturePath);
                    if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
                    string targetMasterImg = Path.Combine(swMasterAparenciasDir, $"{cleanMaterialName}_texture{ext}");
                    if (!string.Equals(Path.GetFullPath(properties.Appearance.TexturePath), Path.GetFullPath(targetMasterImg), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(properties.Appearance.TexturePath, targetMasterImg, true);
                    }
                }
                catch { }
            }

            // 5. Aplica o material na peça ativa e ativa a textura com Mapeamento Automático
            ApplyToActivePart(sldMatPath, cleanMaterialName, properties, appearanceSelection);

            return sldMatPath;
        }

        /// <summary>
        /// Cria ou atualiza o arquivo .sldmat preservando outros materiais já gerados pelo usuário
        /// e mantendo a biblioteca desbloqueada como Biblioteca Personalizada (sem matid/envdata de sistema).
        /// </summary>
        public static void SaveOrUpdateMaterialInLibrary(
            string sldMatPath,
            string materialName,
            string additionalContext,
            MaterialProperties props,
            MaterialAppearanceSelection appearanceSelection = null)
        {
            var doc = new XmlDocument();
            XmlElement root = null;

            if (File.Exists(sldMatPath))
            {
                try
                {
                    doc.Load(sldMatPath);
                    root = doc.DocumentElement;
                }
                catch
                {
                    doc = new XmlDocument();
                    root = null;
                }
            }

            if (root == null)
            {
                string initialXml =
                    "<?xml version=\"1.0\" encoding=\"UTF-16\" standalone=\"no\"?>\r\n" +
                    "<mstns:materials xmlns:mstns=\"http://www.solidworks.com/sldmaterials\" " +
                    "xmlns:msdata=\"urn:schemas-microsoft-com:xml-msdata\" " +
                    "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                    "xmlns:schemaLocation=\"http://matwebtest.matweb.com/sw/XSD/sldmaterials.xsd\" " +
                    "xmlns:sldcolorswatch=\"http://www.solidworks.com/sldcolorswatch\" " +
                    "version=\"2008.03\">\r\n" +
                    "  <curves id=\"curve0\">\r\n" +
                    "    <point x=\"1.0\" y=\"1.0\" />\r\n" +
                    "    <point x=\"2.0\" y=\"1.0\" />\r\n" +
                    "    <point x=\"3.0\" y=\"1.0\" />\r\n" +
                    "  </curves>\r\n" +
                    "</mstns:materials>";

                doc.LoadXml(initialXml);
                root = doc.DocumentElement;
            }

            // Remove atributos de bloqueio (matid / envdata) de quaisquer materiais antigos no arquivo
            // para garantir que toda a biblioteca AutoMaterial tenha o ícone azul (editável no SolidWorks)
            XmlNodeList allExistingMaterials = root.SelectNodes("//classification/material");
            if (allExistingMaterials != null)
            {
                foreach (XmlNode node in allExistingMaterials)
                {
                    if (node is XmlElement matElem)
                    {
                        matElem.RemoveAttribute("matid");
                        matElem.RemoveAttribute("envdata");

                        // Remove também caso o material de mesmo nome já exista em outra categoria
                        if (string.Equals(matElem.GetAttribute("name"), materialName, StringComparison.OrdinalIgnoreCase))
                        {
                            matElem.ParentNode?.RemoveChild(matElem);
                        }
                    }
                }
            }

            string categoryName = string.IsNullOrWhiteSpace(props.ClassificationCategory)
                ? "Materiais Gerados por IA"
                : props.ClassificationCategory.Trim();

            // Localiza ou cria o nó <classification name="...">
            XmlElement classificationElem = null;
            foreach (XmlNode child in root.ChildNodes)
            {
                if (child is XmlElement elem &&
                    string.Equals(elem.LocalName, "classification", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(elem.GetAttribute("name"), categoryName, StringComparison.OrdinalIgnoreCase))
                {
                    classificationElem = elem;
                    break;
                }
            }

            if (classificationElem == null)
            {
                classificationElem = doc.CreateElement("classification");
                classificationElem.SetAttribute("name", categoryName);
                root.AppendChild(classificationElem);
            }

            // Constrói o elemento <material> completo e validado
            XmlElement newMaterialElem = BuildMaterialElement(doc, materialName, additionalContext, props, appearanceSelection);
            classificationElem.AppendChild(newMaterialElem);

            // Grava em UTF-16 LE (formato nativo esperado pelo parser XML do SolidWorks)
            var settings = new XmlWriterSettings
            {
                Encoding = Encoding.Unicode,
                Indent = true,
                IndentChars = "  ",
                OmitXmlDeclaration = false
            };

            using (var writer = XmlWriter.Create(sldMatPath, settings))
            {
                doc.Save(writer);
            }
        }

        private static XmlElement BuildMaterialElement(
            XmlDocument doc,
            string materialName,
            string additionalContext,
            MaterialProperties props,
            MaterialAppearanceSelection appearanceSelection = null)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            ShaderPreset preset = ResolveAppearancePreset(materialName, additionalContext, props.AppearanceFamily, props.ClassificationCategory);

            if (props.Appearance == null)
                props.Appearance = new MaterialAppearance();
            props.Appearance.ShaderName = preset.PwShader2Name;
            props.Appearance.ShaderPath = preset.PwShader2Path;
            props.Appearance.TexturePath = preset.SwTexturePath;

            // Se o usuário selecionou uma textura específica ou imagem personalizada
            bool useRgbOnly = appearanceSelection != null && appearanceSelection.UseRgbOnly;
            string customTexturePath = appearanceSelection?.SelectedImagePath;
            if (!useRgbOnly && !string.IsNullOrWhiteSpace(customTexturePath) && File.Exists(customTexturePath))
            {
                props.Appearance.TexturePath = customTexturePath;
            }

            string descriptionText = !string.IsNullOrWhiteSpace(props.TechnicalDescription)
                ? props.TechnicalDescription.Trim()
                : (!string.IsNullOrWhiteSpace(additionalContext)
                    ? additionalContext.Trim()
                    : $"Material gerado via IA ({props.ModelType ?? "Isotrópico linear elástico"})");

            string propSource = !string.IsNullOrWhiteSpace(props.PropertySource)
                ? props.PropertySource.Trim()
                : "AutoMaterial IA (SI Validated)";

            // Cria <material> SEM matid e SEM envdata para ser uma Biblioteca Personalizada editável
            XmlElement matElem = doc.CreateElement("material");
            matElem.SetAttribute("name", materialName);
            matElem.SetAttribute("description", descriptionText);
            matElem.SetAttribute("propertysource", propSource);
            matElem.SetAttribute("appdata", $"Modelo Constitutivo: {props.ModelType ?? "Isotrópico linear elástico"}");

            // 1. Bloco <shaders> completo (PhotoView 360 + RealView + Textura .jpg)
            XmlElement shadersElem = doc.CreateElement("shaders");

            XmlElement pwShader2 = doc.CreateElement("pwshader2");
            pwShader2.SetAttribute("name", preset.PwShader2Name);
            pwShader2.SetAttribute("isNewShader", "1");
            pwShader2.SetAttribute("path", preset.PwShader2Path);
            shadersElem.AppendChild(pwShader2);

            XmlElement cgShader2 = doc.CreateElement("cgshader2");
            cgShader2.SetAttribute("name", preset.CgShader2Name);
            cgShader2.SetAttribute("ignorescolor", "false");
            shadersElem.AppendChild(cgShader2);

            if (!string.IsNullOrWhiteSpace(preset.PwShaderName))
            {
                XmlElement pwShader = doc.CreateElement("pwshader");
                pwShader.SetAttribute("name", preset.PwShaderName);
                shadersElem.AppendChild(pwShader);
            }

            if (!string.IsNullOrWhiteSpace(preset.CgShaderName))
            {
                XmlElement cgShader = doc.CreateElement("cgshader");
                cgShader.SetAttribute("name", preset.CgShaderName);
                cgShader.SetAttribute("ignorescolor", "false");
                shadersElem.AppendChild(cgShader);
            }

            if (!useRgbOnly && !string.IsNullOrWhiteSpace(props.Appearance.TexturePath))
            {
                // Se o arquivo estiver dentro de "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\data\...",
                // registra o caminho relativo "Images\materials-ia\arquivo.jpg", que é a forma padrão do SolidWorks.
                // Caso contrário, registra o caminho absoluto completo.
                string swDataPrefix = @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\data\";
                string texturePathToRecord = props.Appearance.TexturePath;
                if (texturePathToRecord.StartsWith(swDataPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    texturePathToRecord = texturePathToRecord.Substring(swDataPrefix.Length);
                }

                XmlElement swTexture = doc.CreateElement("swtexture");
                swTexture.SetAttribute("path", texturePathToRecord);
                shadersElem.AppendChild(swTexture);
            }

            matElem.AppendChild(shadersElem);

            // 2. Bloco <swatchcolor RGB="..."> e <sldcolorswatch:Optical ... />
            int r = props.RgbColor?.R ?? 180;
            int g = props.RgbColor?.G ?? 182;
            int b = props.RgbColor?.B ?? 185;
            string hexColor = $"{r:x2}{g:x2}{b:x2}";

            var app = props.Appearance ?? new MaterialAppearance();
            double ambient = app.Ambient > 0 ? Math.Min(1.0, app.Ambient) : 0.70;
            double diffuse = app.Diffuse > 0 ? Math.Min(1.0, app.Diffuse) : 0.85;
            double specularity = app.Specularity >= 0 ? Math.Min(1.0, app.Specularity) : 0.60;
            double shininess = app.Shininess > 0 ? Math.Min(1.0, app.Shininess) : 0.30;
            double transparency = Math.Max(0.0, Math.Min(1.0, app.Transparency));

            XmlElement swatchElem = doc.CreateElement("swatchcolor");
            swatchElem.SetAttribute("RGB", hexColor);

            XmlElement opticalElem = doc.CreateElement("sldcolorswatch", "Optical", ColorSwatchNamespace);
            opticalElem.SetAttribute("Ambient", ambient.ToString("0.000000", inv));
            opticalElem.SetAttribute("Transparency", transparency.ToString("0.000000", inv));
            opticalElem.SetAttribute("Diffuse", diffuse.ToString("0.000000", inv));
            opticalElem.SetAttribute("Specularity", specularity.ToString("0.000000", inv));
            opticalElem.SetAttribute("Shininess", shininess.ToString("0.000000", inv));
            opticalElem.SetAttribute("Emission", "0.000000");
            swatchElem.AppendChild(opticalElem);
            matElem.AppendChild(swatchElem);

            // 3. Bloco <xhatch>
            string xhatchName = !string.IsNullOrWhiteSpace(preset.DefaultXHatch)
                ? preset.DefaultXHatch
                : (!string.IsNullOrWhiteSpace(props.XHatch) ? props.XHatch : "ANSI31 (Iron BrickStone)");

            XmlElement xhatchElem = doc.CreateElement("xhatch");
            xhatchElem.SetAttribute("name", xhatchName);
            xhatchElem.SetAttribute("angle", "0.0");
            xhatchElem.SetAttribute("scale", "1.0");
            matElem.AppendChild(xhatchElem);

            // 4. Bloco <physicalproperties> seguindo rigorosamente a sequência do sldmaterials.xsd
            int modelTypeId = ResolveSolidWorksModelTypeId(props.ModelType);
            bool includeOrthotropicAxes = (modelTypeId == 1); // 1 = swsMaterialModelTypeLinearElasticOrthtropic

            XmlElement physElem = doc.CreateElement("physicalproperties");

            // EX, (EY, EZ)
            AppendPhysProp(doc, physElem, "EX", "Módulo elástico", props.ElasticModulus, inv);
            if (includeOrthotropicAxes)
            {
                AppendPhysProp(doc, physElem, "EY", "Módulo elástico em Y", Math.Round(props.ElasticModulus * 0.45, 0), inv);
                AppendPhysProp(doc, physElem, "EZ", "Módulo elástico em Z", Math.Round(props.ElasticModulus * 0.15, 0), inv);
            }

            // NUXY, (NUYZ, NUXZ)
            AppendPhysProp(doc, physElem, "NUXY", "Coeficiente de Poisson", props.PoissonRatio, inv);
            if (includeOrthotropicAxes)
            {
                AppendPhysProp(doc, physElem, "NUYZ", "Coeficiente de Poisson YZ", props.PoissonRatio, inv);
                AppendPhysProp(doc, physElem, "NUXZ", "Coeficiente de Poisson XZ", props.PoissonRatio, inv);
            }

            // (GXZ, GYZ), GXY
            if (includeOrthotropicAxes)
            {
                AppendPhysProp(doc, physElem, "GXZ", "Módulo de cisalhamento XZ", Math.Round(props.ShearModulus * 0.6, 0), inv);
                AppendPhysProp(doc, physElem, "GYZ", "Módulo de cisalhamento YZ", Math.Round(props.ShearModulus * 0.5, 0), inv);
            }
            AppendPhysProp(doc, physElem, "GXY", "Módulo de cisalhamento", props.ShearModulus, inv);

            // ALPX, (ALPY, ALPZ)
            AppendPhysProp(doc, physElem, "ALPX", "Coeficiente de expansão térmica", props.ThermalExpansionCoefficient, inv, "0.##########");
            if (includeOrthotropicAxes)
            {
                AppendPhysProp(doc, physElem, "ALPY", "Coeficiente de expansão térmica Y", props.ThermalExpansionCoefficient * 1.5, inv, "0.##########");
                AppendPhysProp(doc, physElem, "ALPZ", "Coeficiente de expansão térmica Z", props.ThermalExpansionCoefficient * 2.0, inv, "0.##########");
            }

            // DENS
            AppendPhysProp(doc, physElem, "DENS", "Massa específica", props.MassDensity, inv);

            // KX, (KY, KZ)
            AppendPhysProp(doc, physElem, "KX", "Condutividade térmica", props.ThermalConductivity, inv);
            if (includeOrthotropicAxes)
            {
                AppendPhysProp(doc, physElem, "KY", "Condutividade térmica Y", props.ThermalConductivity, inv);
                AppendPhysProp(doc, physElem, "KZ", "Condutividade térmica Z", props.ThermalConductivity * 0.7, inv);
            }

            // C, SIGXT, SIGXC, SIGYLD, MDAMP
            AppendPhysProp(doc, physElem, "C", "Calor específico", props.SpecificHeat, inv);
            AppendPhysProp(doc, physElem, "SIGXT", "Resistência à tração", props.TensileStrength, inv);
            AppendPhysProp(doc, physElem, "SIGXC", "Resistência à compressão", props.CompressiveStrength, inv);
            AppendPhysProp(doc, physElem, "SIGYLD", "Limite de escoamento", props.YieldStrength, inv);

            if (props.MaterialDamping > 0)
            {
                AppendPhysProp(doc, physElem, "MDAMP", "Coeficiente de amortecimento do material", props.MaterialDamping, inv);
            }

            // <materialmodel name="Material Model Name" type="..." />
            // Nota: Para materiais ortotrópicos (como MDF/madeira), se type="0" (Isotrópico) for usado como padrão de visualização,
            // o SolidWorks mostra a tabela unificada com todas as 10 propriedades preenchidas, e mantemos as componentes ortotrópicas
            // prontas caso o usuário mude o dropdown para Ortotrópico!
            int xmlModelType = (modelTypeId == 1) ? 0 : modelTypeId;
            XmlElement modelElem = doc.CreateElement("materialmodel");
            modelElem.SetAttribute("name", "Material Model Name");
            modelElem.SetAttribute("type", xmlModelType.ToString(inv));
            physElem.AppendChild(modelElem);

            matElem.AppendChild(physElem);

            // 5. Bloco <custom> com metadados de curadoria da IA
            XmlElement customElem = doc.CreateElement("custom");
            AppendCustomProp(doc, customElem, "Modelo Constitutivo Recomendado", "Análise CAE/Simulation", props.ModelType ?? "Isotrópico linear elástico", "");
            AppendCustomProp(doc, customElem, "Critério de Falha", "Resistência dos Materiais", props.FailureCriterion ?? "Max von Mises Stress", "");
            AppendCustomProp(doc, customElem, "Textura Fotográfica", "Arquivo de Textura", preset.SwTexturePath ?? "", "");
            AppendCustomProp(doc, customElem, "Shader PhotoView 360", "Arquivo .p2m", preset.PwShader2Path ?? "", "");
            matElem.AppendChild(customElem);

            return matElem;
        }

        private static void AppendPhysProp(
            XmlDocument doc,
            XmlElement parent,
            string tagName,
            string displayName,
            double value,
            CultureInfo inv,
            string format = "0.######")
        {
            XmlElement el = doc.CreateElement(tagName);
            el.SetAttribute("displayname", displayName);
            el.SetAttribute("value", value.ToString(format, inv));
            parent.AppendChild(el);
        }

        private static void AppendCustomProp(
            XmlDocument doc,
            XmlElement parent,
            string name,
            string description,
            string value,
            string units)
        {
            XmlElement prop = doc.CreateElement("prop");
            prop.SetAttribute("name", name);
            prop.SetAttribute("description", description);
            prop.SetAttribute("value", value);
            prop.SetAttribute("units", units);
            parent.AppendChild(prop);
        }

        /// <summary>
        /// Converte a string do Tipo de Modelo para o enum oficial swsMaterialModelType_e do SolidWorks Simulation.
        /// </summary>
        private static int ResolveSolidWorksModelTypeId(string modelType)
        {
            if (string.IsNullOrWhiteSpace(modelType)) return 0;

            string m = modelType.ToLowerInvariant();
            if (m.Contains("ortotr")) return 1;          // swsMaterialModelTypeLinearElasticOrthtropic
            if (m.Contains("não linear") || m.Contains("nao linear") || m.Contains("nonlinear")) return 3;
            if (m.Contains("von mises")) return 4;       // swsMaterialModelTypeElastoPlasticvonMisesKinematic
            if (m.Contains("tresca")) return 5;          // swsMaterialModelTypeElastoPlasticTrescaKinematic
            if (m.Contains("drucker")) return 6;         // swsMaterialModelTypeElastoPlasticDruckerPrager
            if (m.Contains("mooney")) return 7;          // swsMaterialModelTypeHyperElasticMooneyRivlin
            if (m.Contains("ogden")) return 8;           // swsMaterialModelTypeHyperElasticOgden
            if (m.Contains("blatz")) return 9;           // swsMaterialModelTypeHyperElasticBlatzko
            if (m.Contains("visco")) return 10;          // swsMaterialModelTypeViscoElastic
            if (m.Contains("nitinol")) return 11;        // swsMaterialModelTypeNitinol

            return 0; // swsMaterialModelTypeLinearElasticIsotropic
        }

        /// <summary>
        /// Mapeia o material para os arquivos REAIS de textura (.jpg) e shaders (.p2m / cgshader2)
        /// verificados na instalação oficial do SolidWorks.
        /// </summary>
        private static ShaderPreset ResolveAppearancePreset(
            string materialName,
            string additionalContext,
            string appearanceFamily,
            string category)
        {
            string combined = $"{materialName} {additionalContext} {appearanceFamily} {category}".ToLowerInvariant();
            string fam = (appearanceFamily ?? "").ToLowerInvariant();

            // 1. Madeiras, MDF, Compensados, Celulose
            if (fam.StartsWith("wood") || combined.Contains("mdf") || combined.Contains("madeira") ||
                combined.Contains("compensado") || combined.Contains("pinus") || combined.Contains("pinho") ||
                combined.Contains("carvalho") || combined.Contains("mogno") || combined.Contains("faia") ||
                combined.Contains("balsa") || combined.Contains("cedro") || combined.Contains("teca") ||
                combined.Contains("bambu") || combined.Contains("aglomerado") || combined.Contains("hdf"))
            {
                if (fam.Contains("mahogany") || combined.Contains("mogno") || combined.Contains("imbuia") || combined.Contains("ipê"))
                {
                    return new ShaderPreset
                    {
                        PwShader2Name = "polished mahogany 2d",
                        PwShader2Path = @"\organic\wood\mahogany\polished mahogany 2d.p2m",
                        CgShader2Name = "polishedmahogany",
                        PwShaderName = "mahogany",
                        CgShaderName = "WoodMahogany",
                        SwTexturePath = @"images\textures\wood\mahogany.jpg",
                        DefaultXHatch = "ISO (aço)"
                    };
                }
                if (fam.Contains("oak") || combined.Contains("carvalho") || combined.Contains("peroba"))
                {
                    return new ShaderPreset
                    {
                        PwShader2Name = "polished oak 2d",
                        PwShader2Path = @"\organic\wood\oak\polished oak 2d.p2m",
                        CgShader2Name = "polishedoak",
                        PwShaderName = "varnished japanese oak",
                        CgShaderName = "WoodOak",
                        SwTexturePath = @"images\textures\wood\grain1.jpg",
                        DefaultXHatch = "ISO (aço)"
                    };
                }
                if (fam.Contains("teak") || combined.Contains("teca") || combined.Contains("nogueira"))
                {
                    return new ShaderPreset
                    {
                        PwShader2Name = "polished teak 2d",
                        PwShader2Path = @"\organic\wood\teak\polished teak 2d.p2m",
                        CgShader2Name = "polishedteak",
                        PwShaderName = "varnished teak",
                        CgShaderName = "WoodTeak",
                        SwTexturePath = @"images\textures\wood\grain2.jpg",
                        DefaultXHatch = "ISO (aço)"
                    };
                }
                if (fam.Contains("pine") || combined.Contains("pinho") || combined.Contains("pinus") || combined.Contains("cedro"))
                {
                    return new ShaderPreset
                    {
                        PwShader2Name = "polished pine 2d",
                        PwShader2Path = @"\organic\wood\pine\polished pine 2d.p2m",
                        CgShader2Name = "polishedpine",
                        PwShaderName = "polished pine",
                        CgShaderName = "WoodPine",
                        SwTexturePath = @"images\textures\wood\pine.jpg",
                        DefaultXHatch = "ISO (aço)"
                    };
                }
                if (fam.Contains("maple") || combined.Contains("bordo") || combined.Contains("marfim"))
                {
                    return new ShaderPreset
                    {
                        PwShader2Name = "polished maple 2d",
                        PwShader2Path = @"\organic\wood\maple\polished maple 2d.p2m",
                        CgShader2Name = "polishedmaple",
                        PwShaderName = "maple",
                        CgShaderName = "WoodMaple",
                        SwTexturePath = @"images\textures\wood\maple.jpg",
                        DefaultXHatch = "ISO (aço)"
                    };
                }

                // Padrão para MDF, Faia, Balsa e madeiras homogêneas claras
                return new ShaderPreset
                {
                    PwShader2Name = "polished beech 2d",
                    PwShader2Path = @"\organic\wood\beech\polished beech 2d.p2m",
                    CgShader2Name = "polishedbeech",
                    PwShaderName = "beech",
                    CgShaderName = "WoodBeech",
                    SwTexturePath = @"images\textures\wood\beech.jpg",
                    DefaultXHatch = "ISO (aço)"
                };
            }

            // 2. Fibra de Carbono / Compósitos de Carbono
            if (fam.Contains("carbon") || combined.Contains("carbono") || combined.Contains("carbon fiber") || combined.Contains("cfrp"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "carbon fiber eproxy 2d",
                    PwShader2Path = @"\plastic\composite\carbon fiber eproxy 2d.p2m",
                    CgShader2Name = "carbonfiberepoxy",
                    PwShaderName = "composite carbon fibre0",
                    CgShaderName = "CarbonFibre",
                    SwTexturePath = @"images\textures\fibers\carbonfiber.jpg",
                    DefaultXHatch = "ANSI34 (Plastic Rubber)"
                };
            }

            // 3. Fibra de Vidro / Compósitos de Vidro
            if (fam.Contains("glass_fiber") || combined.Contains("fibra de vidro") || combined.Contains("glass fiber") || combined.Contains("gfrp") || combined.Contains("fr4"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "glass fiber",
                    PwShader2Path = @"\glass\textured\glass fiber.p2m",
                    CgShader2Name = "GlassFibre",
                    PwShaderName = "polished plastic1",
                    CgShaderName = "GlassFibre",
                    SwTexturePath = @"images\textures\fibers\glassfiber.jpg",
                    DefaultXHatch = "ANSI34 (Plastic Rubber)"
                };
            }

            // 4. Borrachas, Elastômeros, Silicones, Poliuretanos Flexíveis
            if (fam.StartsWith("rubber") || combined.Contains("borracha") || combined.Contains("nbr") ||
                combined.Contains("epdm") || combined.Contains("viton") || combined.Contains("neoprene") ||
                combined.Contains("elast") || combined.Contains("silicone") || combined.Contains("sbr"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "glossy rubber",
                    PwShader2Path = @"\rubber\gloss\glossy rubber.p2m",
                    CgShader2Name = "glossyrubber",
                    PwShaderName = "rubber",
                    CgShaderName = "RubberSoft",
                    SwTexturePath = @"images\textures\plastic\polished\pplastic2.jpg",
                    DefaultXHatch = "ANSI34 (Plastic Rubber)"
                };
            }

            // 5. Vidros, Acrílicos e Polímeros Transparentes
            if (fam.Contains("glass_clear") || combined.Contains("vidro") || combined.Contains("borossilicato"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "clear glass",
                    PwShader2Path = @"\glass\gloss\clear glass.p2m",
                    CgShader2Name = "clearglass",
                    PwShaderName = "glass",
                    CgShaderName = "Glass",
                    SwTexturePath = @"images\textures\silicon\silicondioxide.jpg",
                    DefaultXHatch = "ANSI33 (Bronze Brass)"
                };
            }

            // 6. Plásticos e Polímeros (ABS, Nylon, Policarbonato, PEEK, POM, PP, PEAD, PTFE, PETG, PLA)
            if (fam.StartsWith("plastic") || combined.Contains("plástic") || combined.Contains("plastic") ||
                combined.Contains("nylon") || combined.Contains("poliamida") || combined.Contains("abs") ||
                combined.Contains("policarbonato") || combined.Contains("acríl") || combined.Contains("pmma") ||
                combined.Contains("peek") || combined.Contains("acetal") || combined.Contains("pom") ||
                combined.Contains("delrin") || combined.Contains("polietileno") || combined.Contains("pead") ||
                combined.Contains("uhmw") || combined.Contains("polipropileno") || combined.Contains("teflon") ||
                combined.Contains("ptfe") || combined.Contains("pvc") || combined.Contains("pet") || combined.Contains("pla"))
            {
                if (fam.Contains("clear") || combined.Contains("policarbonato") || combined.Contains("acríl") || combined.Contains("transparente"))
                {
                    return new ShaderPreset
                    {
                        PwShader2Name = "polycarbonate.p2m",
                        PwShader2Path = @"\plastic\clear plastic\polycarbonate.p2m",
                        CgShader2Name = "polycarbonateplastic",
                        PwShaderName = "clear ionomer resin",
                        CgShaderName = "PlasticPolycarbonate",
                        SwTexturePath = @"images\textures\plastic\polished\pplastic2.jpg",
                        DefaultXHatch = "ANSI34 (Plastic Rubber)"
                    };
                }

                return new ShaderPreset
                {
                    PwShader2Name = "White-PW-MT11050",
                    PwShader2Path = @"\plastic\textured\White-PW-MT11050.p2m",
                    CgShader2Name = "PlasticPolystyrene",
                    PwShaderName = "ionomer resin",
                    CgShaderName = "PlasticPolystyrene",
                    SwTexturePath = @"images\textures\plastic\polished\pplastic2.jpg",
                    DefaultXHatch = "ANSI34 (Plastic Rubber)"
                };
            }

            // 7. Cobre, Latão e Bronze
            if (fam.Contains("copper") || combined.Contains("cobre") || combined.Contains("copper"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "polished copper",
                    PwShader2Path = @"\metal\copper\polished copper.p2m",
                    CgShader2Name = "polishedcopper",
                    PwShaderName = "copper",
                    CgShaderName = "CopperPolished",
                    SwTexturePath = @"images\textures\metal\cast\cast_fine.jpg",
                    DefaultXHatch = "ANSI33 (Bronze Brass)"
                };
            }
            if (fam.Contains("brass") || combined.Contains("latão") || combined.Contains("latao") || combined.Contains("brass"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "polished brass",
                    PwShader2Path = @"\metal\brass\polished brass.p2m",
                    CgShader2Name = "polishedbrass",
                    PwShaderName = "brass",
                    CgShaderName = "BrassPolished",
                    SwTexturePath = @"images\textures\metal\cast\cast_fine.jpg",
                    DefaultXHatch = "ANSI33 (Bronze Brass)"
                };
            }
            if (fam.Contains("bronze") || combined.Contains("bronze"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "polished bronze",
                    PwShader2Path = @"\metal\bronze\polished bronze.p2m",
                    CgShader2Name = "BronzeAluminum",
                    PwShaderName = "burnished bronze",
                    CgShaderName = "BronzeAluminum",
                    SwTexturePath = @"images\textures\metal\cast\cast_fine.jpg",
                    DefaultXHatch = "ANSI33 (Bronze Brass)"
                };
            }

            // 8. Alumínio e suas Ligas
            if (fam.StartsWith("aluminum") || combined.Contains("alumínio") || combined.Contains("aluminio") || combined.Contains("aluminum") ||
                combined.Contains("6061") || combined.Contains("7075") || combined.Contains("5052") || combined.Contains("2024"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "satin finish aluminum",
                    PwShader2Path = @"\metal\aluminum\satin finish aluminum.p2m",
                    CgShader2Name = "satinfinishaluminum",
                    PwShaderName = "polished aluminum",
                    CgShaderName = "AluminumSatin",
                    SwTexturePath = @"images\textures\metal\cast\cast_fine.jpg",
                    DefaultXHatch = "ANSI38 (Aluminum)"
                };
            }

            // 9. Ferro Fundido
            if (fam.Contains("iron_cast") || combined.Contains("ferro fundido") || combined.Contains("cast iron") ||
                combined.Contains("nodular") || combined.Contains("cinzento") || combined.Contains("ggg") || combined.Contains("fc-"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "cast iron",
                    PwShader2Path = @"\metal\iron\cast iron.p2m",
                    CgShader2Name = "castiron",
                    PwShaderName = "cast iron",
                    CgShaderName = "IronDuctile",
                    SwTexturePath = @"images\textures\metal\cast\cast_rough.jpg",
                    DefaultXHatch = "ANSI31 (Iron BrickStone)"
                };
            }

            // 10. Titânio / Níquel / Inconel / Zinco
            if (fam.Contains("titanium") || combined.Contains("titânio") || combined.Contains("titanio") || combined.Contains("ti-6al"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "satin finish titanium",
                    PwShader2Path = @"\metal\titanium\satin finish titanium.p2m",
                    CgShader2Name = "satinfinishnickel",
                    PwShaderName = "brushed nickel",
                    CgShaderName = "NickelBrushed",
                    SwTexturePath = @"images\textures\metal\brushed\brshmtl8.jpg",
                    DefaultXHatch = "ANSI32 (Steel)"
                };
            }
            if (fam.Contains("nickel") || combined.Contains("níquel") || combined.Contains("inconel") || combined.Contains("monel") || combined.Contains("nitinol"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "Brushed Nickel",
                    PwShader2Path = @"\metal\nickel\brushed nickel.p2m",
                    CgShader2Name = "brushednickel",
                    PwShaderName = "brushed nickel",
                    CgShaderName = "NickelBrushed",
                    SwTexturePath = @"images\textures\metal\brushed\brshmtl8.jpg",
                    DefaultXHatch = "ANSI32 (Steel)"
                };
            }
            if (fam.Contains("chrome") || combined.Contains("cromado") || combined.Contains("cromo"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "chromium plate",
                    PwShader2Path = @"\metal\chrome\chromium plate.p2m",
                    CgShader2Name = "PolishedSteel",
                    PwShaderName = "chrome",
                    CgShaderName = "ChromiumPlate",
                    SwTexturePath = @"images\textures\metal\polished\chrome6.jpg",
                    DefaultXHatch = "ANSI32 (Steel)"
                };
            }
            if (fam.Contains("galvanized") || combined.Contains("galvanizado") || combined.Contains("zincado"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "shiny galvanized",
                    PwShader2Path = @"\metal\galvanized\shiny galvanized.p2m",
                    CgShader2Name = "PolishedSteel",
                    PwShaderName = "galvanized",
                    CgShaderName = "SteelGalvanized",
                    SwTexturePath = @"images\textures\metal\brushed\galvnisd2.jpg",
                    DefaultXHatch = "ANSI32 (Steel)"
                };
            }
            if (fam.Contains("ceramic") || fam.Contains("stone") || combined.Contains("cerâmic") || combined.Contains("porcelana") ||
                combined.Contains("alumina") || combined.Contains("concreto") || combined.Contains("granito") || combined.Contains("mármore"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "porcelaine",
                    PwShader2Path = @"\stone\stoneware\porcelaine.p2m",
                    CgShader2Name = "porcelain",
                    PwShaderName = "porcelain",
                    CgShaderName = "ceramicporcelain",
                    SwTexturePath = @"images\textures\metal\cast\cast_rough.jpg",
                    DefaultXHatch = "ANSI31 (Iron BrickStone)"
                };
            }

            // 11. Aços (Inox, Escovado, Fundido, Polido/Carbono)
            if (fam.Contains("stainless") || combined.Contains("inox") || combined.Contains("304") || combined.Contains("316") || combined.Contains("420"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "satin finish stainless steel",
                    PwShader2Path = @"\metal\steel\satin finish stainless steel.p2m",
                    CgShader2Name = "PolishedSteel",
                    PwShaderName = "stainless steel",
                    CgShaderName = "SteelAISI304",
                    SwTexturePath = @"images\textures\metal\brushed\brshmtl8.jpg",
                    DefaultXHatch = "ANSI32 (Steel)"
                };
            }
            if (fam.Contains("cast") || combined.Contains("fundido"))
            {
                return new ShaderPreset
                {
                    PwShader2Name = "cast carbon steel",
                    PwShader2Path = @"\metal\steel\cast carbon steel.p2m",
                    CgShader2Name = "castcarbonsteel",
                    PwShaderName = "polished steel",
                    CgShaderName = "SteelCastCarbon",
                    SwTexturePath = @"images\textures\metal\cast\cast_fine.jpg",
                    DefaultXHatch = "ANSI32 (Steel)"
                };
            }

            // Padrão Aço Polido / Usinado (AISI 1020, 1045, 4140, 4340, etc.)
            return new ShaderPreset
            {
                PwShader2Name = "polished steel",
                PwShader2Path = @"\metal\steel\polished steel.p2m",
                CgShader2Name = "PolishedSteel",
                PwShaderName = "stainless steel",
                CgShaderName = "SteelAISI1020",
                SwTexturePath = @"images\textures\metal\cast\cast_fine.jpg",
                DefaultXHatch = "ANSI32 (Steel)"
            };
        }

        private void EnsurePathRegistered(swUserPreferenceStringValue_e pref, string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;
            try
            {
                string currentPaths = _swApp.GetUserPreferenceStringValue((int)pref) ?? string.Empty;

                string[] paths = currentPaths.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string p in paths)
                {
                    if (string.Equals(p.Trim(), folderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                string updatedPaths = string.IsNullOrWhiteSpace(currentPaths)
                    ? folderPath
                    : currentPaths.TrimEnd(';') + ";" + folderPath;

                _swApp.SetUserPreferenceStringValue((int)pref, updatedPaths);
            }
            catch { }
        }

        private void EnsureMaterialDatabaseFolderRegistered(string folderPath)
        {
            EnsurePathRegistered(swUserPreferenceStringValue_e.swFileLocationsMaterialDatabases, folderPath);
        }

        private void EnsureTextureFolderRegistered(string folderPath)
        {
            EnsurePathRegistered(swUserPreferenceStringValue_e.swFileLocationsTextures, folderPath);
            EnsurePathRegistered(swUserPreferenceStringValue_e.swFileLocationsAppearances, folderPath);
            EnsurePathRegistered(swUserPreferenceStringValue_e.swFileLocationsCustomAppearances, folderPath);
        }

        /// <summary>
        /// Aplica o material na peça ativa e ativa as propriedades visuais nativas do material
        /// (Textura .jpg + Shader .p2m + RealView + BlendColor) sem sobrepor a textura com cor chapada!
        /// </summary>
        private void ApplyToActivePart(string sldMatFilePath, string materialName, MaterialProperties props, MaterialAppearanceSelection appearanceSelection = null)
        {
            IModelDoc2 modelDoc = null;
            IModelDocExtension modelExt = null;
            IPartDoc partDoc = null;
            IConfigurationManager configMgr = null;
            IConfiguration activeConfig = null;
            MaterialVisualPropertiesData visProps = null;

            try
            {
                modelDoc = _swApp.ActiveDoc as IModelDoc2;
                if (modelDoc == null || modelDoc.GetType() != (int)swDocumentTypes_e.swDocPART)
                {
                    throw new InvalidOperationException("Abra uma Peça (.SLDPRT) ativa no SolidWorks antes de gerar o material.");
                }

                partDoc = (IPartDoc)modelDoc;
                modelExt = modelDoc.Extension;
                configMgr = modelDoc.ConfigurationManager;
                activeConfig = configMgr?.ActiveConfiguration;
                string configName = activeConfig != null ? activeConfig.Name : "Default";

                // 1. Remove qualquer override de cor antiga na peça que esconderia a textura (.jpg) do material
                try
                {
                    modelExt?.RemoveMaterialProperty((int)swInConfigurationOpts_e.swAllConfiguration, null);
                }
                catch { }

                // 2. Aplica o material na configuração ativa da peça apontando para o arquivo .sldmat
                partDoc.SetMaterialPropertyName2(configName, sldMatFilePath, materialName);

                string appliedMaterial = partDoc.GetMaterialPropertyName2(configName, out _);
                if (string.IsNullOrEmpty(appliedMaterial))
                {
                    string fileNameOnly = Path.GetFileName(sldMatFilePath);
                    partDoc.SetMaterialPropertyName2(configName, fileNameOnly, materialName);
                }

                // 3. Garante que o SolidWorks aplique a Aparência/Textura (.jpg + .p2m) e a Cor do Material à peça
                try
                {
                    visProps = partDoc.GetMaterialVisualProperties();
                    if (visProps != null)
                    {
                        bool useRgbOnly = appearanceSelection != null && appearanceSelection.UseRgbOnly;
                        visProps.ApplyAppearance = !useRgbOnly;
                        visProps.ApplyMaterialColorToPart = true;
                        visProps.ApplyMaterialHatchToSection = true;
                        visProps.RealView = true;
                        visProps.AdvancedGraphics = true;
                        visProps.BlendColor = !useRgbOnly;

                        partDoc.SetMaterialVisualProperties(
                            visProps,
                            (int)swInConfigurationOpts_e.swAllConfiguration,
                            null);
                    }
                }
                catch { }

                // Se o usuário optou por apenas cor RGB sólida, define a cor na peça
                if (appearanceSelection != null && appearanceSelection.UseRgbOnly && props.RgbColor != null)
                {
                    try
                    {
                        double[] matProps = new double[9];
                        matProps[0] = props.RgbColor.R / 255.0; // Red
                        matProps[1] = props.RgbColor.G / 255.0; // Green
                        matProps[2] = props.RgbColor.B / 255.0; // Blue
                        matProps[3] = 0.5; // Ambient
                        matProps[4] = 0.8; // Diffuse
                        matProps[5] = 0.5; // Specularity
                        matProps[6] = 0.3; // Shininess
                        matProps[7] = 0.0; // Transparency
                        matProps[8] = 0.0; // Emission
                        modelDoc.MaterialPropertyValues = matProps;
                    }
                    catch { }
                }

                // 4. Aplica RenderMaterial com TextureFilename diretamente na peça ativa
                // Isso preenche e ativa o PropertyManager 'textura' mostrado na interface do SolidWorks
                // vinculando a geometria (AddEntity) e os estados de exibição imediatamente.
                if (appearanceSelection != null && !appearanceSelection.UseRgbOnly && !string.IsNullOrWhiteSpace(props.Appearance?.TexturePath))
                {
                    try
                    {
                        ApplyTextureRenderMaterial(modelDoc, modelExt, partDoc, activeConfig, props.Appearance.TexturePath, props);
                    }
                    catch { }
                }

                // Atualiza os materiais de renderização no SceneGraph 3D e reconstrói a peça
                try
                {
                    modelExt?.UpdateRenderMaterialsInSceneGraph(true);
                }
                catch { }

                try
                {
                    modelDoc.Rebuild((int)swRebuildOptions_e.swRebuildAll);
                }
                catch
                {
                    modelDoc.EditRebuild3();
                }
                modelDoc.GraphicsRedraw2();
            }
            finally
            {
                ReleaseComObject(visProps);
                ReleaseComObject(activeConfig);
                ReleaseComObject(configMgr);
                ReleaseComObject(modelExt);
                ReleaseComObject(modelDoc);
            }
        }

        /// <summary>
        /// Cria e associa um RenderMaterial com TextureFilename apontando para a imagem da textura,
        /// vinculando a entidade geométrica (AddEntity) para exibição imediata na viewport 3D.
        /// </summary>
        private static void ApplyTextureRenderMaterial(
            IModelDoc2 modelDoc,
            IModelDocExtension modelExt,
            IPartDoc partDoc,
            IConfiguration activeConfig,
            string textureFilePath,
            MaterialProperties props)
        {
            if (modelExt == null || string.IsNullOrWhiteSpace(textureFilePath))
                return;

            string resolvedTexturePath = textureFilePath;
            if (!File.Exists(resolvedTexturePath))
            {
                string fullSwPath = Path.Combine(@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\data", textureFilePath);
                if (File.Exists(fullSwPath))
                {
                    resolvedTexturePath = fullSwPath;
                }
            }

            if (!File.Exists(resolvedTexturePath))
                return;

            try
            {
                // Remove aparências / render materials anteriores para evitar conflito de renderização
                try
                {
                    int count = modelExt.GetRenderMaterialsCount();
                    if (count > 0)
                    {
                        object[] materials = modelExt.GetRenderMaterials() as object[];
                        if (materials != null)
                        {
                            foreach (object item in materials)
                            {
                                if (item is RenderMaterial oldMat)
                                {
                                    modelExt.DeleteRenderMaterial(oldMat.MaterialID, false);
                                }
                            }
                        }
                    }
                }
                catch { }

                // Utiliza o shader nativo texture.p2m do SolidWorks ou cria render material
                string p2mPath = @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\data\graphics\materials\texture.p2m";
                RenderMaterial renderMat = null;
                if (File.Exists(p2mPath))
                {
                    renderMat = modelExt.CreateRenderMaterial(p2mPath);
                }
                else
                {
                    renderMat = modelExt.CreateRenderMaterial("");
                }

                if (renderMat != null)
                {
                    renderMat.TextureFilename = resolvedTexturePath;
                    renderMat.MappingType = 4; // 4 = Automático (Box / Auto-mapping no SolidWorks: projeta a textura em todos os eixos do modelo)

                    if (props.RgbColor != null)
                    {
                        // Converte RGB (0-255) para inteiro COLORREF do Windows: R + (G << 8) + (B << 16)
                        int colorRef = props.RgbColor.R | (props.RgbColor.G << 8) | (props.RgbColor.B << 16);
                        renderMat.PrimaryColor = colorRef;
                    }

                    // 1. Vincula a geometria da peça inteira (swModel / modelDoc)
                    // Isso é o vínculo fundamental que faltava: sem o AddEntity, o SolidWorks registrava o RenderMaterial
                    // na memória mas deixava a lista de geometria não confirmada, só aplicando quando o usuário clicava em ✔️.
                    bool entityAdded = false;
                    try
                    {
                        if (modelDoc != null)
                        {
                            entityAdded = renderMat.AddEntity(modelDoc);
                        }
                    }
                    catch { }

                    // Vincula também todos os corpos sólidos (solid bodies) existentes na peça para garantia máxima
                    if (partDoc != null)
                    {
                        try
                        {
                            object[] bodies = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                            if (bodies != null && bodies.Length > 0)
                            {
                                foreach (object b in bodies)
                                {
                                    if (b != null)
                                    {
                                        renderMat.AddEntity(b);
                                        entityAdded = true;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    // 2. Registra o Render Material no documento e nos estados de exibição
                    bool materialAdded = false;
                    try
                    {
                        object[] displayStates = activeConfig != null ? activeConfig.GetDisplayStates() as object[] : null;
                        if (displayStates != null && displayStates.Length > 0)
                        {
                            materialAdded = modelExt.AddDisplayStateSpecificRenderMaterial(
                                renderMat,
                                (int)swDisplayStateOpts_e.swAllDisplayState,
                                displayStates,
                                out _,
                                out _);
                        }
                    }
                    catch { }

                    if (!materialAdded)
                    {
                        try
                        {
                            modelExt.AddRenderMaterial(renderMat, out _);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static void ReleaseComObject(object comObj)
        {
            if (comObj != null && Marshal.IsComObject(comObj))
            {
                try { Marshal.ReleaseComObject(comObj); } catch { }
            }
        }
    }
}
