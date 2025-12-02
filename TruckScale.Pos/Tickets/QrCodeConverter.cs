using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using QRCoder;
using System.Drawing;
using System.Drawing.Imaging;

namespace TruckScale.Pos.Tickets
{
    public class QrCodeConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var payload = value as string;
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            using var qr = new QRCode(data);
            using Bitmap bmp = qr.GetGraphic(20); // 20 = nivel de zoom

            return BitmapToImageSource(bmp);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static BitmapImage BitmapToImageSource(Bitmap bitmap)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
    }
}
