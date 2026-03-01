// Desambiguación WPF vs WinForms.
// UseWindowsForms=true es requerido por SigPlusNET (hereda de WinForms UserControl).
// Los aliases aquí dan prioridad explícita a los tipos WPF sobre los de WinForms.
global using Application    = System.Windows.Application;
global using Button         = System.Windows.Controls.Button;
global using TextBox        = System.Windows.Controls.TextBox;
global using UserControl    = System.Windows.Controls.UserControl;
global using KeyEventArgs   = System.Windows.Input.KeyEventArgs;
global using MessageBox     = System.Windows.MessageBox;
global using Brush          = System.Windows.Media.Brush;
global using Brushes        = System.Windows.Media.Brushes;
global using Color          = System.Windows.Media.Color;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using DataFormats    = System.Windows.DataFormats;
global using Size           = System.Windows.Size;
