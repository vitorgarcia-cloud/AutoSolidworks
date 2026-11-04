using System.Runtime.Serialization;

namespace AutoMaterial.Models
{
    /// <summary>
    /// DTO que representa o retorno estrito (Structured Output) da LLM
    /// com todas as propriedades físicas exigidas pelo SolidWorks no Sistema Internacional (SI),
    /// o modelo constitutivo do SolidWorks Simulation e a definição de aparência fotorrealista.
    /// </summary>
    [DataContract]
    public class MaterialProperties
    {
        /// <summary>
        /// Módulo de Elasticidade / Young na direção principal X (EX) em N/m² (Pa).
        /// </summary>
        [DataMember(Name = "elastic_modulus")]
        public double ElasticModulus { get; set; }

        /// <summary>
        /// Coeficiente de Poisson (NUXY) - Adimensional.
        /// </summary>
        [DataMember(Name = "poisson_ratio")]
        public double PoissonRatio { get; set; }

        /// <summary>
        /// Módulo de Cisalhamento (GXY) em N/m² (Pa).
        /// </summary>
        [DataMember(Name = "shear_modulus")]
        public double ShearModulus { get; set; }

        /// <summary>
        /// Massa Específica / Densidade (DENS) em kg/m³.
        /// </summary>
        [DataMember(Name = "mass_density")]
        public double MassDensity { get; set; }

        /// <summary>
        /// Limite de Resistência à Tração (SIGXT) em N/m² (Pa).
        /// </summary>
        [DataMember(Name = "tensile_strength")]
        public double TensileStrength { get; set; }

        /// <summary>
        /// Limite de Resistência à Compressão (SIGXC) em N/m² (Pa).
        /// </summary>
        [DataMember(Name = "compressive_strength")]
        public double CompressiveStrength { get; set; }

        /// <summary>
        /// Limite de Escoamento / Limite Elástico Proporcional (SIGYLD) em N/m² (Pa).
        /// </summary>
        [DataMember(Name = "yield_strength")]
        public double YieldStrength { get; set; }

        /// <summary>
        /// Coeficiente de Expansão Térmica Linear (ALPX) em 1/K (/K).
        /// </summary>
        [DataMember(Name = "thermal_expansion_coefficient")]
        public double ThermalExpansionCoefficient { get; set; }

        /// <summary>
        /// Condutividade Térmica (KX) em W/(m·K).
        /// </summary>
        [DataMember(Name = "thermal_conductivity")]
        public double ThermalConductivity { get; set; }

        /// <summary>
        /// Calor Específico (C) em J/(kg·K).
        /// </summary>
        [DataMember(Name = "specific_heat")]
        public double SpecificHeat { get; set; }

        /// <summary>
        /// Coeficiente de Amortecimento do Material (MDAMP) - Adimensional (ex: 0.01 a 0.05).
        /// </summary>
        [DataMember(Name = "material_damping")]
        public double MaterialDamping { get; set; }

        /// <summary>
        /// Tipo de modelo constitutivo do SolidWorks Simulation:
        /// "Isotrópico linear elástico", "Ortotrópico linear elástico", "Elástico não linear",
        /// "Plasticidade - von Mises", "Plasticidade - Tresca", "Plasticidade - Drucker Prager",
        /// "Hiperelástico - Mooney Rivlin", "Hiperelástico - Ogden", "Hiperelástico - Blatz Ko",
        /// "Viscoelástico", "Nitinol".
        /// </summary>
        [DataMember(Name = "model_type")]
        public string ModelType { get; set; }

        /// <summary>
        /// Categoria da classificação do material em português (ex: "Aços", "Ligas de Alumínio", "Madeiras", "Plásticos", "Borrachas e Elastômeros", "Compósitos", "Ligas de Cobre").
        /// </summary>
        [DataMember(Name = "classification_category")]
        public string ClassificationCategory { get; set; }

        /// <summary>
        /// Fonte / Origem técnica dos dados (ex: "Norma ABNT / ASTM / MatWeb / Literatura Técnica").
        /// </summary>
        [DataMember(Name = "property_source")]
        public string PropertySource { get; set; }

        /// <summary>
        /// Descrição técnica detalhada do material e sua aplicação.
        /// </summary>
        [DataMember(Name = "technical_description")]
        public string TechnicalDescription { get; set; }

        /// <summary>
        /// Critério de falha recomendado (ex: "Max von Mises Stress", "Mohr-Coulomb Stress", "Max Normal Stress", "Max Shear Stress (Tresca)").
        /// </summary>
        [DataMember(Name = "failure_criterion")]
        public string FailureCriterion { get; set; }

        /// <summary>
        /// Padrão de hachura para desenhos 2D (ex: "ANSI32 (Steel)", "ANSI31 (Iron BrickStone)", "ANSI33 (Bronze Brass)", "ANSI34 (Plastic Rubber)", "ISO (aço)").
        /// </summary>
        [DataMember(Name = "xhatch")]
        public string XHatch { get; set; }

        /// <summary>
        /// Identificador da família visual de textura/shader nativa do SolidWorks:
        /// Ex: "wood_oak", "wood_beech", "wood_pine", "wood_mahogany", "wood_maple", "wood_teak",
        /// "steel_polished", "steel_brushed", "steel_matte", "steel_cast", "stainless_satin", "chrome",
        /// "iron_cast", "aluminum_satin", "aluminum_brushed", "aluminum_cast",
        /// "copper_polished", "brass_polished", "bronze_polished", "titanium_satin", "zinc_matte",
        /// "plastic_polished", "plastic_textured", "plastic_clear", "rubber_glossy", "rubber_matte",
        /// "carbon_fiber", "glass_fiber", "glass_clear", "ceramic_porcelain", "stone_concrete".
        /// </summary>
        [DataMember(Name = "appearance_family")]
        public string AppearanceFamily { get; set; }

        /// <summary>
        /// Definição de aparência realista com textura de imagem (.jpg), shader PhotoView 360 (.p2m) e propriedades ópticas.
        /// </summary>
        [DataMember(Name = "appearance")]
        public MaterialAppearance Appearance { get; set; }

        /// <summary>
        /// Cor RGB representativa do material (0 a 255).
        /// </summary>
        [DataMember(Name = "rgb_color")]
        public RgbColor RgbColor { get; set; }
    }

    [DataContract]
    public class MaterialAppearance
    {
        [DataMember(Name = "texture_path")]
        public string TexturePath { get; set; }

        [DataMember(Name = "shader_path")]
        public string ShaderPath { get; set; }

        [DataMember(Name = "shader_name")]
        public string ShaderName { get; set; }

        [DataMember(Name = "specularity")]
        public double Specularity { get; set; } = 0.5;

        [DataMember(Name = "shininess")]
        public double Shininess { get; set; } = 0.3;

        [DataMember(Name = "diffuse")]
        public double Diffuse { get; set; } = 0.8;

        [DataMember(Name = "ambient")]
        public double Ambient { get; set; } = 0.5;

        [DataMember(Name = "transparency")]
        public double Transparency { get; set; } = 0.0;
    }

    [DataContract]
    public class RgbColor
    {
        [DataMember(Name = "r")]
        public int R { get; set; }

        [DataMember(Name = "g")]
        public int G { get; set; }

        [DataMember(Name = "b")]
        public int B { get; set; }
    }
}
