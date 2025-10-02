using MaterialDesignThemes.Wpf;
using System.Windows.Media;

namespace TruckScale.Pos.Services
{
    public static class ThemeService
    {
        private static readonly PaletteHelper _palette = new();

        public static void Apply(bool dark, string? primaryHex = null, string? secondaryHex = null)
        {
            var theme = _palette.GetTheme();

            theme.SetBaseTheme(dark ? BaseTheme.Dark : BaseTheme.Light);

            if (!string.IsNullOrWhiteSpace(primaryHex))
                theme.SetPrimaryColor((Color)ColorConverter.ConvertFromString(primaryHex)!);

            if (!string.IsNullOrWhiteSpace(secondaryHex))
                theme.SetSecondaryColor((Color)ColorConverter.ConvertFromString(secondaryHex)!);

            _palette.SetTheme(theme);
        }
    }
}
