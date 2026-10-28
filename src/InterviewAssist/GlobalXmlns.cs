// Maps the app's XAML-facing namespaces to MAUI's global XML namespace, so a XAML file that declares
// xmlns:g="http://schemas.microsoft.com/dotnet/maui/global" can use any of these types with one prefix.
[assembly: XmlnsDefinition("http://schemas.microsoft.com/dotnet/maui/global", "InterviewAssist")]
[assembly: XmlnsDefinition("http://schemas.microsoft.com/dotnet/maui/global", "InterviewAssist.Views")]
[assembly: XmlnsDefinition("http://schemas.microsoft.com/dotnet/maui/global", "InterviewAssist.Presentation.ViewModels", AssemblyName = "InterviewAssist.Presentation")]
[assembly: XmlnsDefinition("http://schemas.microsoft.com/dotnet/maui/global", "InterviewAssist.Controls")]
[assembly: XmlnsDefinition("http://schemas.microsoft.com/dotnet/maui/global", "InterviewAssist.Converters")]
