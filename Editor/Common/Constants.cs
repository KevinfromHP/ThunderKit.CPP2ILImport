using System.IO;

namespace ThunderKit.CPP2ILImport.Common
{
    public static class Constants
    {
        public const string CPP2ILImport = "CPP2ILImport";

        public const string CPP2ILImportPackageName = "com.kevinfromhp.thunderkit.cpp2ilimport";

        public const string CPP2ILImportRoot = "Packages/" + CPP2ILImportPackageName;
        public const string CPP2ILExePath = CPP2ILImportRoot + "/Editor/ThirdParty/Cpp2IL.exe";

        public static readonly string CPP2ILTempDir = Path.Combine(ThunderKit.Common.Constants.TempDir, CPP2ILImport);
    }
}
