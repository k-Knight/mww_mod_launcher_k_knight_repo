using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ILRepacking;

class Program {
    static void Main(string[] args) {
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string targetDir = Path.Combine(appDataPath, "WizardWarsModLoader");

        string targetExeName = "Magicka Wizard Wars Mod Loader.exe";
        string targetExe = Path.Combine(targetDir, targetExeName);
        string intermediateExe = Path.Combine(targetDir, "launcher_bridged.exe");
        string outputExe = targetExe;

        if (!File.Exists(targetExe)) {
            Console.WriteLine($"[ERROR] Target executable not found at: {targetExe}");
            Console.ReadLine();

            return;
        }

        try {
            Console.WriteLine("[INSTALLER] Extracting embedded payload and dependencies...");

            var currentAssembly = Assembly.GetExecutingAssembly();
            string[] allResourceNames = currentAssembly.GetManifestResourceNames();

            string payloadResourceName = allResourceNames.FirstOrDefault(name => name.IndexOf("k_knight_mod_repo.dll", StringComparison.OrdinalIgnoreCase) >= 0);
            string harmonyResourceName = allResourceNames.FirstOrDefault(name => name.IndexOf("0Harmony.dll", StringComparison.OrdinalIgnoreCase) >= 0);

            if (payloadResourceName == null || harmonyResourceName == null) {
                Console.WriteLine("\n[ERROR] Could not find embedded resources. Here are the files actually embedded in this EXE:");

                if (allResourceNames.Length == 0)
                    Console.WriteLine(" -> (No resources found at all! Check your Build Action in Visual Studio)");
                else {
                    foreach (var name in allResourceNames)
                        Console.WriteLine($" -> {name}");
                }

                throw new Exception("Resource extraction failed. Ensure files are marked as 'Embedded Resource' in their file properties.");
            }

            using (Stream payloadStream = currentAssembly.GetManifestResourceStream(payloadResourceName))
            using (Stream harmonyStream = currentAssembly.GetManifestResourceStream(harmonyResourceName)) {

                if (payloadStream == null || harmonyStream == null)
                    throw new Exception("Could not find embedded resources. Check your namespace and resource names.");

                Console.WriteLine("[CECIL] Injecting single bootstrap connection point...");

                var payloadAssembly = AssemblyDefinition.ReadAssembly(payloadStream);
                var bootMethod = payloadAssembly.MainModule.Types
                    .First(t => t.Namespace == "MyModRepoOverrides" && t.Name == "OverridesBootstrap")
                    .Methods.First(m => m.Name == "Start");

                using (var assembly = AssemblyDefinition.ReadAssembly(targetExe, new ReaderParameters { ReadWrite = true })) {
                    Console.WriteLine("[INSTALLER] Publicizing target executable in memory...");

                    foreach (var type in assembly.MainModule.Types) {
                        if (type.IsNotPublic)
                            type.IsPublic = true;
                        if (type.IsNestedPrivate || type.IsNestedAssembly)
                            type.IsNestedPublic = true;

                        foreach (var field in type.Fields)
                            if (field.IsPrivate || field.IsAssembly)
                                field.IsPublic = true;

                        foreach (var method in type.Methods)
                            if (method.IsPrivate || method.IsAssembly)
                                method.IsPublic = true;
                    }

                    Console.WriteLine("[CECIL] Injecting single bootstrap connection point...");
                    var il = assembly.EntryPoint.Body.GetILProcessor();
                    il.InsertBefore(assembly.EntryPoint.Body.Instructions.First(), il.Create(OpCodes.Call, assembly.MainModule.ImportReference(bootMethod)));

                    if (assembly.Name.HasPublicKey) {
                        assembly.Name.PublicKey = assembly.Name.PublicKeyToken = null;
                        assembly.MainModule.Attributes &= ~ModuleAttributes.StrongNameSigned;
                    }

                    assembly.Write(intermediateExe);
                }

                Console.WriteLine("[ILREPACK] Merging types from memory streams...");

                string tempPayloadPath = Path.Combine(targetDir, "temp_payload.dll");
                string tempHarmonyPath = Path.Combine(targetDir, "temp_harmony.dll");

                payloadStream.Position = 0;
                harmonyStream.Position = 0;

                using (var fs = File.Create(tempPayloadPath)) payloadStream.CopyTo(fs);
                using (var fs = File.Create(tempHarmonyPath)) harmonyStream.CopyTo(fs);

                var repackOptions = new RepackOptions {
                    OutputFile = outputExe,
                    TargetKind = ILRepack.Kind.WinExe,
                    Internalize = true,
                    CopyAttributes = true,
                    InputAssemblies = new string[] { intermediateExe, tempPayloadPath, tempHarmonyPath }
                };

                var customLogger = new CustomLogger();
                var repacker = new ILRepack(repackOptions, customLogger);
                repacker.Repack();

                SafeDelete(intermediateExe);
                SafeDelete(tempPayloadPath);
                SafeDelete(tempHarmonyPath);

                Console.WriteLine("[SUCCESS] Mod Loader successfully updated and bundled in AppData!");
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"Patcher Error: {ex.Message}");

            if (ex.StackTrace != null)
                Console.WriteLine(ex.StackTrace);
        }
        Console.ReadLine();
    }

    static void SafeDelete(string path) {
        try {
            if (File.Exists(path))
                File.Delete(path);
        } catch { }
    }
}

public class CustomLogger : ILRepacking.ILogger {
    public bool ShouldLogVerbose { get; set; } = false;
    public void Log(object message) => Console.WriteLine($"[ILREPACK] {message}");
    public void Msg(string message) => Console.WriteLine($"[ILREPACK] {message}");
    public void Info(string message) => Console.WriteLine($"[ILREPACK] {message}");
    public void Warn(string message) => Console.WriteLine($"[ILREPACK-WARN] {message}");
    public void Error(string message) => Console.WriteLine($"[ILREPACK-ERROR] {message}");
    public void Verbose(string message) {
        if (ShouldLogVerbose)
            Console.WriteLine($"[ILREPACK-VERBOSE] {message}");
    }
}
