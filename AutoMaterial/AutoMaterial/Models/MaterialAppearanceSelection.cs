namespace AutoMaterial.Models
{
    public enum AppearanceMode
    {
        InternetTexture,
        CustomFile,
        RgbOnly
    }

    public class MaterialAppearanceSelection
    {
        public AppearanceMode Mode { get; set; } = AppearanceMode.InternetTexture;
        public string SelectedImagePath { get; set; }
        public string SelectedImageTitle { get; set; }
        public bool UseRgbOnly => Mode == AppearanceMode.RgbOnly;
    }
}
