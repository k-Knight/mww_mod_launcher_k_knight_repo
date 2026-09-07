using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ILRepacking;

class Program {
    static void Main(string[] args) {
        string targetExe = @".\orig_launcher-publicized.exe";
        string outputExe = @".\Magicka Wizard Wars Mod Loader.exe";
        string payloadDll = @".\k_knight_mod_repo.dll";
        string harmonyDll = @".\0Harmony.dll";
        string intermediateExe = @".\launcher_bridged.exe";

        try {
            Console.WriteLine("[CECIL] Injecting single bootstrap connection point...");
            var payloadAssembly = AssemblyDefinition.ReadAssembly(payloadDll);
            var bootMethod = payloadAssembly.MainModule.Types
                .First(t => t.Namespace == "MyModRepoOverrides" && t.Name == "OverridesBootstrap")
                .Methods.First(m => m.Name == "Start");

            using (var assembly = AssemblyDefinition.ReadAssembly(targetExe, new ReaderParameters { ReadWrite = true })) {
                var il = assembly.EntryPoint.Body.GetILProcessor();
                il.InsertBefore(assembly.EntryPoint.Body.Instructions.First(), il.Create(OpCodes.Call, assembly.MainModule.ImportReference(bootMethod)));

                if (assembly.Name.HasPublicKey) {
                    assembly.Name.PublicKey = assembly.Name.PublicKeyToken = null;
                    assembly.MainModule.Attributes &= ~ModuleAttributes.StrongNameSigned;
                }
                assembly.Write(intermediateExe);
            }

            Console.WriteLine("[ILREPACK] Merging types using programmatic parameters...");

            var repackOptions = new RepackOptions();

            repackOptions.OutputFile = outputExe;
            repackOptions.TargetKind = ILRepack.Kind.WinExe;
            repackOptions.Internalize = true;
            repackOptions.CopyAttributes = true;

            repackOptions.InputAssemblies = new string[] { intermediateExe, payloadDll, harmonyDll };

            var customLogger = new CustomLogger();
            var repacker = new ILRepack(repackOptions, customLogger);

            repacker.Repack();

            if (File.Exists(intermediateExe)) File.Delete(intermediateExe);
            Console.WriteLine("[SUCCESS] Single-file standalone executable compiled cleanly!");
        }
        catch (Exception ex) {
            Console.WriteLine($"Patcher Error: {ex.Message}");
            if (ex.StackTrace != null) Console.WriteLine(ex.StackTrace);
        }
        Console.ReadLine();
    }
}

public class CustomLogger : ILRepacking.ILogger {
    public bool ShouldLogVerbose { get; set; } = false;
    public void Log(object message) => Console.WriteLine($"[ILREPACK] {message}");
    public void Msg(string message) => Console.WriteLine($"[ILREPACK] {message}");
    public void Info(string message) => Console.WriteLine($"[ILREPACK] {message}");
    public void Warn(string message) => Console.WriteLine($"[ILREPACK-WARN] {message}");
    public void Error(string message) => Console.WriteLine($"[ILREPACK-ERROR] {message}");
    public void Verbose(string message) { if (ShouldLogVerbose) Console.WriteLine($"[ILREPACK-VERBOSE] {message}"); }
}
