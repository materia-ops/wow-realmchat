namespace RealmChat
{
    // Pinned values for the realm's chat brain. These are deliberately the
    // ONLY tuning knobs that ship in the (public) binary - nothing here is
    // environment-specific. Site-specific values (extra firewall subnets, an
    // expected DNS name) live only in the machine-local config.json, entered
    // once at first run; the local LAN subnet is derived from the machine's
    // own adapter at runtime.
    //
    // Changing a value here and releasing rolls it to the host PC via
    // self-update - no hands on the machine.
    public static class Constants
    {
        public const string OllamaVersion = "0.32.0";                     // pinned release
        public const string Model = "llama3.1:8b-instruct-q4_K_M";        // exact tag (quantization matters)
        public const string KeepAlive = "4h";                             // OLLAMA_KEEP_ALIVE
        public const int DefaultPort = 11434;                             // Ollama default

        public const string ModelsDir = @"C:\ProgramData\Ollama\models";  // shared store

        // Same rule identity the original PowerShell setup created, so repair
        // updates it in place instead of duplicating it.
        public const string FwRuleName = "Ollama-LAN-Inbound-TCP-11434";
        public const string FwDisplay = "Ollama (LAN only, TCP 11434)";

        public static string InstallerUrl
        {
            get
            {
                return "https://github.com/ollama/ollama/releases/download/v" +
                       OllamaVersion + "/OllamaSetup.exe";
            }
        }
    }
}
