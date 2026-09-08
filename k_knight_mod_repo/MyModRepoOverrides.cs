using HarmonyLib;
using MagickaMods;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Net.Mime.MediaTypeNames;
using Image = System.Drawing.Image;

namespace MyModRepoOverrides {
    public class CustomModpack : Modpack {
        public string DownloadLink { get; set; } = null;

        public CustomModpack(Modpack basePack, string downloadLink = null) : base(basePack.Name) {
            this.Description = basePack.Description;
            this.LongDescription = basePack.LongDescription;
            this.DependencyVersion = basePack.DependencyVersion;
            this.Author = basePack.Author;
            this.URLIcon = basePack.URLIcon;
            this.Enabled = basePack.Enabled;
            this.HasLua = basePack.HasLua;
            this.HasPNG = basePack.HasPNG;
            this.ModIcon = basePack.ModIcon;
            this.ModCard = basePack.ModCard;
            this.Files = basePack.Files;
            this.EnabledMods = basePack.EnabledMods;

            this.DownloadLink = downloadLink;
        }
    }

    [HarmonyPatch(typeof(MagickaMods.MainFormManager), MethodType.Constructor)]
    public static class MainFormManagerSizePatch {
        [HarmonyPostfix]
        public static void Postfix(Form __instance) {
            try {
                int newWidth = (int)Math.Round(__instance.Width * 1.25);
                int newHeight = (int)Math.Round(__instance.Height * 1.25);

                __instance.Size = new Size(newWidth, newHeight);

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[MOD] Main UI Resized successfully to: {newWidth}x{newHeight}");
                Console.ResetColor();
            }
            catch (Exception ex) {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[MOD ERROR] Failed to resize MainFormManager: {ex.Message}");
                Console.ResetColor();
                System.IO.File.AppendAllText("mod_error.txt", $"[Resize Error] {ex}\n");
            }
        }
    }

    [HarmonyPatch(typeof(MagickaMods.MainFormManager), nameof(MainFormManager.InitializeComponent))]
    public static class OverridesBootstrap {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        public static void Start() {
            try {
                var harmony = new Harmony("com.magickamods.patcher");
                harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());
            }
            catch (Exception ex) {
                System.IO.File.WriteAllText("mod_error.txt", ex.ToString());
            }
        }

#if DEBUG
        [HarmonyPrefix]
        public static bool Prefix() {
            try {
                AllocConsole();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[MOD] Diagnostic Console Initialized!");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("[MOD] Handing control back to the original InitializeComponent() routine...");
                Console.ResetColor();
            }
            catch (Exception ex) {
                Console.WriteLine($"[MOD ERROR] Console allocation failed: {ex.Message}");
            }

            return true;
        }
#endif

        [HarmonyPostfix]
        public static void Postfix(System.Windows.Forms.Form __instance) {
            string repoFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mod_repositories.txt");
            if (!System.IO.File.Exists(repoFile)) {
                System.IO.File.WriteAllText(repoFile, "https://raw.githubusercontent.com/k-Knight/mww-mods-build/master/\n");
            }
            ModDownloadHelper.DownloadModList();
        }
    }

    [HarmonyPatch(typeof(MagickaMods.ModDownloadHelper), nameof(ModDownloadHelper.DownloadModList))]
    public static class DownloadMostListPatcher {

        private static readonly object CollectionLock = new object();
        private static readonly object DownloadModListLock = new object();
        private static readonly string REPO_FILE_PATH = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mod_repositories.txt");

        [HarmonyPrefix]
        public static bool Prefix() {
            lock (DownloadModListLock) {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[MOD] Thread-Safe Multi-Repo Parallel DownloadModList invoked!");
                Console.ResetColor();

                try {
                    if (ModDownloadHelper.DownloadedModpacks != null) {
                        Utilities.MainForm.SortModListPanel(Utilities.MainForm.PAN_DownloadModList, ModDownloadHelper.DownloadedModpacks);
                        return false;
                    }

                    var downloadedModpacks = new ModpackManager();
                    ModDownloadHelper.DownloadedModpacks = new ModpackManager();
                    FileHelper.ClearFolder(FileHelper.DOWNLOAD_FOLDER);

                    BackgroundWorker backgroundWorker = new BackgroundWorker();
                    backgroundWorker.DoWork += delegate {
                        try {
                            List<string> repositories = new List<string>();

                            if (File.Exists(REPO_FILE_PATH)) {
                                string[] customRepos = File.ReadAllLines(REPO_FILE_PATH);
                                foreach (string repo in customRepos) {
                                    if (!string.IsNullOrWhiteSpace(repo)) {
                                        string trimmedRepo = repo.Trim();

                                        if (!trimmedRepo.EndsWith("/"))
                                            trimmedRepo += "/";

                                        repositories.Add(trimmedRepo);
                                    }
                                }
                            }

                            repositories.Add(WebInterface.SITE_PREFIX);

                            ConcurrentDictionary<string, string> parallelRawLists = new ConcurrentDictionary<string, string>();

                            Console.ForegroundColor = ConsoleColor.Blue;
                            Console.WriteLine("[MOD] Initiating parallel network fetching of raw mod repositories lists...");
                            Console.ResetColor();

                            Parallel.ForEach(repositories, repoPrefix => {
                                try {
                                    string currentModListFile = repoPrefix.Contains("NylonAphro") ? ModDownloadHelper.MOD_LIST : "modlist.txt";
                                    string rawList = WebInterface.RetrieveFileString(repoPrefix + currentModListFile);

                                    if (!string.IsNullOrWhiteSpace(rawList)) {
                                        parallelRawLists.TryAdd(repoPrefix, rawList);
                                    }
                                }
                                catch (Exception repoFetchEx) {
                                    lock (CollectionLock) {
                                        Console.ForegroundColor = ConsoleColor.Red;
                                        Console.WriteLine($"[REPO FETCH ERROR] Parallel retrieval failed for {repoPrefix}: {repoFetchEx.Message}");
                                        Console.ResetColor();
                                    }
                                }
                            });

                            foreach (var kvp in parallelRawLists) {
                                string repoPrefix = kvp.Key;
                                string rawList = kvp.Value;

                                try {
                                    Console.ForegroundColor = ConsoleColor.Blue;
                                    Console.WriteLine($"[MOD] Sequentially executing parsed files for repository: {repoPrefix}");
                                    Console.ResetColor();

                                    string[] lines = rawList.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                                    foreach (string text in lines) {
                                        try {
                                            string trimmedText = text.Trim();

                                            if (string.IsNullOrEmpty(trimmedText))
                                                continue;

                                            string downloadPath = Path.Combine(FileHelper.DOWNLOAD_FOLDER, trimmedText);

                                            Console.ForegroundColor = ConsoleColor.Cyan;
                                            Console.WriteLine($"[SEQUENTIAL PROCESS] Processing: {trimmedText} from {repoPrefix}");
                                            Console.ResetColor();

                                            string assetUrl = repoPrefix.Contains("NylonAphro") ? WebInterface.MOD_SAMPLE_PREFIX + trimmedText : repoPrefix + "mod_previews/" + trimmedText;

                                            WebInterface.RetrieveFile(assetUrl, downloadPath);

                                            Modpack modpack = ModManager.ImportSampleModpack(downloadPath);
                                            if (modpack != null) {
                                                string currentDownloadLink = repoPrefix.Contains("NylonAphro") ? WebInterface.MOD_PREFIX + modpack.Name + ".mww" : repoPrefix + "mod_files/" + modpack.Name + ".mww";
                                                CustomModpack custom_pack = new CustomModpack(modpack, currentDownloadLink);

                                                downloadedModpacks.AddMod(custom_pack);
                                            }
                                        }
                                        catch (Exception itemEx) {
                                            Console.ForegroundColor = ConsoleColor.Red;
                                            Console.WriteLine($"[ITEM ERROR] Failed fetching file {text.Trim()}: {itemEx.Message}");
                                            Console.ResetColor();
                                        }
                                    }
                                }
                                catch (Exception repoEx) {
                                    Console.ForegroundColor = ConsoleColor.Red;
                                    Console.WriteLine($"[REPO PROCESSING ERROR] Failed processing sequential repository loop {repoPrefix}: {repoEx.Message}");
                                    lock (CollectionLock) { Console.ResetColor(); }
                                }
                            }

                            ModDownloadHelper.DownloadedModpacks = downloadedModpacks;

                            if (Utilities.MainForm.InvokeRequired) {
                                Utilities.MainForm.BeginInvoke(new Action(() => {
                                    Utilities.MainForm.SortModListPanel(Utilities.MainForm.PAN_DownloadModList, ModDownloadHelper.DownloadedModpacks);
                                }));
                            }
                            else {
                                Utilities.MainForm.SortModListPanel(Utilities.MainForm.PAN_DownloadModList, ModDownloadHelper.DownloadedModpacks);
                            }

                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("[MOD] All multi-repository sequence routines completed successfully.");
                            Console.ResetColor();
                        }
                        catch (Exception loopEx) {
                            Console.WriteLine($"[MOD LOOP ERROR] Critical error inside background worker loop: {loopEx.Message}");
                        }
                    };

                    backgroundWorker.RunWorkerAsync();

                }
                catch (Exception ex) {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[MOD CRASH] Error in parallel replacement function: {ex.Message}");
                    Console.ResetColor();
                }
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(MagickaMods.DownloadModCard), nameof(MagickaMods.DownloadModCard.SetModData))]
    public static class DownloadModCardSetModDataPatch {
        public static readonly Dictionary<DownloadModCard, string> CardRepoMap = new Dictionary<DownloadModCard, string>();

        private static void ScaleFontsRecursive(Control root, float factor) {
            foreach (Control child in root.Controls) {
                if (child.Font != null) {
                    child.Font = new Font(child.Font.FontFamily, child.Font.Size * factor, child.Font.Style);
                }
                if (child.Controls.Count > 0) {
                    ScaleFontsRecursive(child, factor);
                }
            }
        }

        [HarmonyPrefix]
        public static bool SetModData_Prefix(DownloadModCard __instance, Modpack mod) {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[DEBUG] Mod check invoked. Incoming object runtime type: {mod?.GetType().FullName ?? "null"} (Name: '{mod?.Name ?? "Unknown"}')");
            Console.ResetColor();

            if (!(mod is CustomModpack customMod) || string.IsNullOrEmpty(customMod.DownloadLink)) {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[DEBUG] Mod '{mod?.Name ?? "Unknown"}' classified as normal. Passing execution back to vanilla base game methods.");
                Console.ResetColor();

                return true;
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[PATCH] Custom mod layout triggered for: '{customMod.Name}' -> {customMod.DownloadLink}");
            Console.ResetColor();

            __instance.Name = customMod.Name;

            __instance.Icon = new PictureBox {
                Name = "mod_icon",
                Size = new Size(110, 110),
                Location = new Point(230, 45),
                SizeMode = PictureBoxSizeMode.Zoom,
                Visible = true
            };
            if (customMod.ModIcon != null) {
                __instance.Icon.Image = new Bitmap(customMod.ModIcon);
            }
            __instance.Controls.Add(__instance.Icon);

            __instance.Title = new Label {
                Name = "title",
                Location = new Point(17, 25),
                ForeColor = Color.White,
                Font = Utilities.POPPINS_BOLD_TITLE,
                Text = customMod.Name,
                MaximumSize = new Size(265, 55),
                AutoSize = true
            };
            __instance.Controls.Add(__instance.Title);

            __instance.Description = new Label {
                Name = "description",
                Location = new Point(20, 80),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = Utilities.POPPINS_BOLD,
                Text = customMod.Description,
                MaximumSize = new Size(200, 95),
                AutoSize = true
            };
            __instance.Controls.Add(__instance.Description);

            __instance.Download = new Label {
                Location = new Point(150, 172),
                Font = Utilities.POPPINS_BOLD,
                Text = "Download",
                AutoSize = true,
                ForeColor = Utilities.DefaultLabelColor,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            __instance.Controls.Add(__instance.Download);

            __instance.Download.MouseEnter += delegate {
                __instance.Download.ForeColor = Color.White;
            };
            __instance.Download.MouseLeave += delegate {
                __instance.Download.ForeColor = Utilities.DefaultLabelColor;
            };

            __instance.Download.Click += delegate {
                try {
                    string text = WebInterface.DOWNLOAD_FOLDER + "\\" + customMod.Name + ".mww";
                    if (!Utilities.KeypairListHasKeyValue(Utilities.TASK_SCHEDULE, "import", text)) {
                        Utilities.BUILD_IN_PROGRESS = true;
                        if (File.Exists(text)) {
                            File.Delete(text);
                        }

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[PATCH DOWNLOAD] Fetching custom repository target: {customMod.DownloadLink}");
                        Console.ResetColor();

                        WebInterface.DownloadFile(customMod.DownloadLink, text);

                        if (!Directory.Exists(WebInterface.DOWNLOAD_FOLDER + "\\temp")) {
                            Directory.CreateDirectory(WebInterface.DOWNLOAD_FOLDER + "\\temp");
                        }

                        Utilities.ScheduleTask("import", text);
                        Utilities.ScheduleTask("build_all", "");
                    }
                }
                catch (Exception ex) {
                    Utilities.PrintLog(ex.Message);
                }

                Utilities.BUILD_IN_PROGRESS = false;
            };

            Utilities.SetDoubleBuffer(__instance, true);

            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(DownloadModCard __instance, Modpack mod) {
            if (mod is CustomModpack customMod && !string.IsNullOrEmpty(customMod.DownloadLink)) {
                string repoName = "Custom Repository";
                try {
                    if (customMod.DownloadLink.Contains("raw.githubusercontent.com")) {
                        string trimmed = customMod.DownloadLink.Replace("https://raw.githubusercontent.com/", "");
                        string[] segments = trimmed.Split('/');
                        if (segments.Length >= 2) {
                            repoName = $"{segments[0]} / {segments[1]}";
                        }
                    }
                    else if (customMod.DownloadLink.Contains("github.com")) {
                        string trimmed = customMod.DownloadLink.Replace("https://github.com", "");
                        string[] segments = trimmed.Split('/');
                        if (segments.Length >= 2) {
                            repoName = $"{segments[0]} / {segments[1]}";
                        }
                    }
                }
                catch {
                    repoName = "Custom Remote Repository";
                }

                CardRepoMap[__instance] = repoName;
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[POSTFIX START] Post-processing card instance for mod: '{mod?.Name ?? "Unknown"}'");

            float factor = 0.88f;
            Console.WriteLine($"[POSTFIX LAYOUT] Applying dimensional adjustment engine matrix: Scale Factor -> {factor} (Original Bounds: {__instance.Size.Width}x{__instance.Size.Height})");

            __instance.SuspendLayout();
            __instance.Scale(new SizeF(factor, factor));

            Control titleCtrl = __instance.Title ?? (__instance.Controls.ContainsKey("title") ? __instance.Controls["title"] : null);
            if (titleCtrl != null && titleCtrl.Font != null) {
                float appliedTitleFactor = factor * 0.82f;
                FontStyle boldStyle = titleCtrl.Font.Style | FontStyle.Bold;
                titleCtrl.Font = new Font(titleCtrl.Font.FontFamily, titleCtrl.Font.Size * appliedTitleFactor, boldStyle);
                Console.WriteLine($"[POSTFIX FONT] Scaled Title Explicitly: Factor -> {appliedTitleFactor}");
            }

            Control descCtrl = __instance.Description ?? (__instance.Controls.ContainsKey("description") ? __instance.Controls["description"] : null);
            if (descCtrl != null && descCtrl.Font != null) {
                float appliedDescFactor = factor * 1.07f;
                descCtrl.Font = new Font(descCtrl.Font.FontFamily, descCtrl.Font.Size * appliedDescFactor, descCtrl.Font.Style);
                Console.WriteLine($"[POSTFIX FONT] Scaled Description Explicitly: Factor -> {appliedDescFactor}");
            }

            Control downloadCtrl = __instance.Download;
            if (downloadCtrl == null) {
                foreach (Control c in __instance.Controls) {
                    if (c is Label && c.Text == "Download") {
                        downloadCtrl = c;
                        break;
                    }
                }
            }
            if (downloadCtrl != null && downloadCtrl.Font != null) {
                float appliedBtnFactor = factor * 1.25f;
                downloadCtrl.Font = new Font(downloadCtrl.Font.FontFamily, downloadCtrl.Font.Size * appliedBtnFactor, downloadCtrl.Font.Style);
                Console.WriteLine($"[POSTFIX FONT] Scaled Download Button/Label Explicitly: Factor -> {appliedBtnFactor}");
            }

            titleCtrl.BringToFront();
            __instance.ResumeLayout(false);

            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"[POSTFIX END] Matrix adjustment complete. New target application bounds: {__instance.Size.Width}x{__instance.Size.Height}");
            Console.ResetColor();
        }
    }

    [HarmonyPatch(typeof(MagickaMods.ModCard), nameof(MagickaMods.ModCard.SetModData))]
    public static class ModCardSetModDataPatch {
        [HarmonyPostfix]
        public static void Postfix(ModCard __instance, Modpack mod) {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[MOD CARD] scaling mod card for  {mod.Name}");
            Console.ResetColor();

            float factor = 0.88f;
            __instance.SuspendLayout();

            __instance.Scale(new SizeF(factor, factor));

            Control descCtrl = __instance.Controls.ContainsKey("description") ? __instance.Controls["description"] : null;
            if (descCtrl != null && descCtrl.Font != null) {
                descCtrl.Location = new Point(descCtrl.Location.X, descCtrl.Location.Y - 20);
                descCtrl.MaximumSize = new Size(175, 70);
                float appliedDescFactor = factor * 1.07f;
                descCtrl.Font = new Font(descCtrl.Font.FontFamily, descCtrl.Font.Size * appliedDescFactor, descCtrl.Font.Style);
                descCtrl.BringToFront();
            }

            Control titleCtrl = __instance.Controls.ContainsKey("title") ? __instance.Controls["title"] : null;
            if (titleCtrl != null && titleCtrl.Font != null) {
                titleCtrl.Location = new Point(titleCtrl.Location.X, titleCtrl.Location.Y - 10);
                titleCtrl.MaximumSize = new Size(235, 55);
                float appliedTitleFactor = factor * 0.82f;
                titleCtrl.Font = new Font(titleCtrl.Font.FontFamily, titleCtrl.Font.Size * appliedTitleFactor, titleCtrl.Font.Style | FontStyle.Bold);
                titleCtrl.BringToFront();
            }

            Control installedIcon = __instance.Controls.ContainsKey("installed_icon") ? __instance.Controls["installed_icon"] : null;
            if (installedIcon != null) {
                int edgePadding = 10;

                int targetX = __instance.Width - installedIcon.Width - edgePadding;
                int targetY = edgePadding;

                installedIcon.Location = new Point(targetX, targetY);
                installedIcon.BringToFront();
            }

            Control[] actionButtons = new Control[5];
            int currentYPosition = 0;

            foreach (Control child in __instance.Controls) {
                if (child is Label label) {
                    if (label.Text == "Enable") actionButtons[0] = label;
                    if (label.Text == "Disable") actionButtons[1] = label;
                    if (label.Text == "Delete") actionButtons[2] = label;
                    if (label.Name == "long_description" || label.Text == "Info") actionButtons[3] = label;
                    if (label.Text == "Update") actionButtons[4] = label;
                }
            }

            float finalButtonScale = factor * 1.33f;
            int totalButtonsWidth = 0;
            int validButtonCount = 0;

            for (int i = 0; i < actionButtons.Length; i++) {
                Control btn = actionButtons[i];
                if (btn != null) {
                    if (btn.Font != null) {
                        btn.Font = new Font(btn.Font.FontFamily, btn.Font.Size * finalButtonScale, btn.Font.Style);
                    }

                    if (btn is Label lbl && lbl.AutoSize) {
                        lbl.Size = TextRenderer.MeasureText(lbl.Text, lbl.Font);
                    }

                    totalButtonsWidth += btn.Width;
                    validButtonCount++;
                    currentYPosition = btn.Location.Y - 3;
                }
            }

            if (validButtonCount > 0) {
                int cardInteriorWidth = __instance.Width;
                int availableWhitespace = cardInteriorWidth - totalButtonsWidth;
                int spacingGapSize = availableWhitespace / (validButtonCount + 1);
                int progressiveXTracker = spacingGapSize;

                for (int i = 0; i < actionButtons.Length; i++) {
                    Control btn = actionButtons[i];
                    if (btn != null) {
                        btn.Location = new Point(progressiveXTracker, currentYPosition);
                        progressiveXTracker += btn.Width + spacingGapSize;
                    }
                }
            }

            __instance.ResumeLayout(false);
        }
    }

    [HarmonyPatch(typeof(MagickaMods.ModManager), nameof(MagickaMods.ModManager.UpdateMod))]
    public static class ModManagerUpdateModPatch {
        [HarmonyPrefix]
        public static bool UpdateMod_Prefix(Modpack mod) {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[DEBUG] Mod check invoked. Incoming object runtime type: {mod?.GetType().FullName ?? "null"} (Name: '{mod?.Name ?? "Unknown"}')");
            Console.ResetColor();

            if (!(mod is CustomModpack customMod) || string.IsNullOrEmpty(customMod.DownloadLink)) {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[DEBUG] Mod '{mod?.Name ?? "Unknown"}' classified as normal. Passing execution back to vanilla base game methods.");
                Console.ResetColor();

                return true;
            }

            try {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[PATCH] Custom update triggered for mod: '{customMod.Name}'");
                Console.ResetColor();

                Utilities.BUILD_IN_PROGRESS = true;
                BackgroundWorker backgroundWorker = new BackgroundWorker();
                backgroundWorker.DoWork += delegate {
                    try {
                        if (customMod.Name.Contains("Script Extender")) {
                            Console.WriteLine($"[UPDATE WORKER] Script Extender keyword matched. Forwarding tracking logic.");
                            Utilities.MODPACKS.DisablePack(customMod.Name);
                            DependencyManager.DownloadScriptExtender();
                        }
                        else {
                            bool enabled = customMod.Enabled;
                            string text = WebInterface.DOWNLOAD_FOLDER + "\\" + customMod.Name + ".mww";

                            if (File.Exists(text)) {
                                Console.WriteLine($"[UPDATE WORKER] Deleting preexisting local file asset at: {text}");
                                File.Delete(text);
                            }

                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"[UPDATE WORKER] Requesting custom update payload via URL: {customMod.DownloadLink}");
                            Console.ResetColor();

                            if (WebInterface.DownloadFile(customMod.DownloadLink, text) != "ERROR:") {
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"[UPDATE WORKER SUCCESS] Download complete. Executing ModManager swap routines for: '{customMod.Name}'");
                                Console.ResetColor();

                                FileHelper.CheckDirectory(WebInterface.DOWNLOAD_FOLDER + "\\temp");

                                ModManager.DeleteModpackSameThread(customMod);

                                Utilities.ScheduleTask("import", text);
                                if (!enabled) {
                                    Console.WriteLine($"[UPDATE WORKER] State tracking preserve applied: keeping '{customMod.Name}' disabled.");
                                    Utilities.ScheduleTask("disable", customMod.Name);
                                }
                            }
                            else {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"[UPDATE WORKER ERROR] WebInterface failed to download from link: {customMod.DownloadLink}");
                                Console.ResetColor();
                                Utilities.PrintLog("Error downloading file " + customMod.Name + " from custom repository as it cannot be found.");
                            }
                        }
                    }
                    catch (Exception threadEx) {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[UPDATE WORKER CRITICAL ERROR] Failure inside worker thread loop: {threadEx.Message}\n{threadEx.StackTrace}");
                        Console.ResetColor();
                        Utilities.PrintLog(threadEx.Message);
                    }

                    Utilities.BUILD_IN_PROGRESS = false;
                    Console.WriteLine($"[UPDATE WORKER] Background thread for '{customMod.Name}' finished.");
                };
                backgroundWorker.RunWorkerAsync();
            }
            catch (Exception ex) {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[PATCH CRITICAL ERROR] Failed to initialize UpdateMod background worker: {ex.Message}");
                Console.ResetColor();
                Utilities.PrintLog(ex.Message);
                Utilities.BUILD_IN_PROGRESS = false;
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(MainFormManager), nameof(MainFormManager.SortModListPanel))]
    public static class SortModListPanelThreeColumnPatch {
        [HarmonyPrefix]
        public static bool Prefix(MainFormManager __instance, Panel modPanel, ModpackManager modpackManager) {
            System.Type resourcesType = AccessTools.TypeByName("MagickaMods.Properties.Resources");

            Utilities.MainForm.Invoke((System.Windows.Forms.MethodInvoker)delegate {
                Utilities.SetDoubleBuffer(modPanel, true);

                modPanel.SuspendLayout();

                if (modPanel.Name.Equals("PAN_DownloadModList")) {
                    if (!modPanel.Controls.ContainsKey("enable_mods_panel")) {
                        var enableLuaModsGetter = AccessTools.PropertyGetter(resourcesType, "Enable_LUA_Mods");
                        Image enableLuaModsImage = (Image)enableLuaModsGetter.Invoke(null, null);

                        PanelDB panelDB = new PanelDB {
                            Name = "enable_mods_panel",
                            BackgroundImage = enableLuaModsImage,
                            BackgroundImageLayout = ImageLayout.Zoom,
                            Size = Utilities.DEFAULT_MOD_ITEM_SIZE,
                            Location = new Point(10, 10),
                            Cursor = Cursors.Hand
                        };

                        var enableClickMethod = AccessTools.Method(typeof(MainFormManager), "EnableModsClicked");
                        if (enableClickMethod != null) {
                            var handler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), __instance, enableClickMethod);
                            panelDB.Click -= handler;
                            panelDB.Click += handler;
                        }
                        modPanel.Controls.Add(panelDB);
                    }

                    if (!modPanel.Controls.ContainsKey("script_extender_panel")) {
                        var scriptExtenderGetter = AccessTools.PropertyGetter(resourcesType, "Script_Extender");
                        Image scriptExtenderImage = (Image)scriptExtenderGetter.Invoke(null, null);

                        PanelDB panelDB2 = new PanelDB {
                            Name = "script_extender_panel",
                            BackgroundImage = scriptExtenderImage,
                            BackgroundImageLayout = ImageLayout.Zoom,
                            Size = Utilities.DEFAULT_MOD_ITEM_SIZE,
                            Location = new Point(20 + Utilities.DEFAULT_MOD_ITEM_SIZE.Width, 10),
                            Cursor = Cursors.Hand
                        };

                        var extenderClickMethod = AccessTools.Method(typeof(MainFormManager), "EnableScriptExtenderClicked");
                        if (extenderClickMethod != null) {
                            var handler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), __instance, extenderClickMethod);
                            panelDB2.Click -= handler;
                            panelDB2.Click += handler;
                        }
                        modPanel.Controls.Add(panelDB2);
                    }
                }

                if (modpackManager != null) {
                    var panModList = (Panel)AccessTools.Field(typeof(MainFormManager), "PAN_ModList").GetValue(__instance);
                    var panDownloadModList = (Panel)AccessTools.Field(typeof(MainFormManager), "PAN_DownloadModList").GetValue(__instance);

                    if (modPanel.Name.Equals(panModList.Name)) {
                        foreach (Modpack mod in modpackManager.Mods) {
                            var modCard = __instance.AddModCard(modPanel, mod);
                        }
                    }
                    else if (modPanel.Name.Equals(panDownloadModList.Name)) {
                        foreach (Modpack mod2 in modpackManager.Mods) {
                            __instance.AddModDownloadCard(modPanel, mod2);
                        }
                    }

                    foreach (Control control4 in modPanel.Controls) {
                        if (control4.Controls.ContainsKey("title")) {
                            Label label = (Label)control4.Controls["title"];
                            if (!modpackManager.Contains(label.Text)) {
                                control4.Dispose();
                            }
                        }
                    }
                }

                Point autoScrollPosition = modPanel.AutoScrollPosition;
                int num = 10;
                int num2 = 0;

                float factor = 0.88f;
                int width = (int)(Utilities.DEFAULT_MOD_ITEM_SIZE.Width * factor);
                int height = (int)(Utilities.DEFAULT_MOD_ITEM_SIZE.Height * factor);

                int maxPanelRightEdge = modPanel.Size.Width - 20;

                bool settingBool = Settings.GetSettingBool("use_flat_mod_cards");

                for (int i = modPanel.Controls.Count - 1; i >= 0; i--) {
                    if (modPanel.Controls[i].Name.StartsWith("DYNAMIC_REPO_HEADER_") ||
                        modPanel.Controls[i].Name.StartsWith("DYNAMIC_REPO_ICON_")) {
                        modPanel.Controls[i].Dispose();
                    }
                }

                List<Control> miscTopPanels = new List<Control>();
                List<Control> vanillaLocalCards = new List<Control>();
                Dictionary<string, List<Control>> onlineRepoGroupedCards = new Dictionary<string, List<Control>>();

                foreach (Control control5 in modPanel.Controls) {
                    if (control5 == null) continue;

                    if (control5 is ModCard modCard && control5.Controls.ContainsKey("installed_icon")) {
                        PictureBox pictureBox = (PictureBox)control5.Controls["installed_icon"];
                        Label label2 = (Label)control5.Controls["description"];
                        Modpack pack = modpackManager.GetPack(control5.Name);

                        if (pack != null) {
                            var greenCheckbox = (Image)AccessTools.PropertyGetter(resourcesType, "green_checkbox").Invoke(null, null);
                            var exclamationMark = (Image)AccessTools.PropertyGetter(resourcesType, "exclamation_mark").Invoke(null, null);
                            var cardDisabled = (Image)AccessTools.PropertyGetter(resourcesType, "CARD_DISABLED").Invoke(null, null);

                            if (pack.IsEnabled()) {
                                pictureBox.Image = greenCheckbox;
                                modCard.BackgroundImage = settingBool ? null : Utilities.GetCardFromInt(pack.ModCard);
                                modCard.BackColor = settingBool ? Utilities.GetCardColorFromInt(pack.ModCard) : Color.Transparent;
                            }
                            else {
                                pictureBox.Image = exclamationMark;
                                modCard.BackgroundImage = settingBool ? null : cardDisabled;
                                modCard.BackColor = settingBool ? Color.Gray : Color.Transparent;
                            }
                            label2.Text = pack.Description;
                        }
                    }

                    if (control5 is DownloadModCard downloadModCard) {
                        Label label3 = (Label)control5.Controls["description"];
                        Modpack pack2 = modpackManager.GetPack(control5.Name);
                        if (pack2 != null) {
                            downloadModCard.BackgroundImage = settingBool ? null : Utilities.GetCardFromInt(pack2.ModCard);
                            downloadModCard.BackColor = settingBool ? Utilities.GetCardColorFromInt(pack2.ModCard) : Color.Transparent;
                            label3.Text = pack2.Description;
                        }
                    }

                    if (control5 is DownloadModCard dmc) {
                        string assignedRepo = DownloadModCardSetModDataPatch.CardRepoMap.ContainsKey(dmc)
                            ? DownloadModCardSetModDataPatch.CardRepoMap[dmc]
                            : "MAIN REPOSITORY";

                        if (!onlineRepoGroupedCards.ContainsKey(assignedRepo)) {
                            onlineRepoGroupedCards[assignedRepo] = new List<Control>();
                        }
                        onlineRepoGroupedCards[assignedRepo].Add(dmc);
                    }
                    else if (control5 is ModCard mc) {
                        vanillaLocalCards.Add(mc);
                    }
                    else {
                        miscTopPanels.Add(control5);
                    }
                }

                foreach (Control staticCtrl in miscTopPanels) {
                    staticCtrl.Location = new Point(num, num2 + autoScrollPosition.Y);
                    if (num + staticCtrl.Width > maxPanelRightEdge) {
                        num = 10;
                        num2 += staticCtrl.Height + 10;
                    }
                    else {
                        num += staticCtrl.Width + 10;
                    }
                }

                if (num > 10) {
                    num = 10;
                    num2 += height + 10;
                }

                foreach (Control localCard in vanillaLocalCards) {
                    int currentItemWidth = (localCard is ModCard) ? width : localCard.Width;
                    int currentItemHeight = (localCard is ModCard) ? height : localCard.Height;

                    if (num + currentItemWidth > maxPanelRightEdge && num > 10) {
                        num = 10;
                        num2 += currentItemHeight + 3;
                    }

                    localCard.Location = new Point(num, num2 + autoScrollPosition.Y);
                    num += currentItemWidth + 10;
                }

                if (vanillaLocalCards.Count > 0 && num > 10) {
                    num = 10;
                    num2 += height + 10;
                }

                num2 += 35;

                foreach (var kvp in onlineRepoGroupedCards) {
                    string currentRepoName = kvp.Key;
                    List<Control> cardsInRepo = kvp.Value;

                    if (cardsInRepo.Count == 0) continue;

                    num = 10;

                    Color panelBackgroundColor = modPanel.BackColor;

                    try {
                        var cultureField = AccessTools.Field(resourcesType, "resourceCulture");
                        object cultureObj = cultureField?.GetValue(null);

                        var resourceManagerProperty = AccessTools.PropertyGetter(resourcesType, "ResourceManager");
                        var managerInstance = (System.Resources.ResourceManager)resourceManagerProperty.Invoke(null, null);

                        Bitmap iconBitmap = (Bitmap)managerInstance.GetObject("MLN_ICN_unpack", (System.Globalization.CultureInfo)cultureObj);

                        if (iconBitmap != null) {
                            PictureBox repoIcon = new PictureBox {
                                Name = $"DYNAMIC_REPO_ICON_{Guid.NewGuid()}",
                                Image = iconBitmap,
                                Size = new Size(28, 28),
                                SizeMode = PictureBoxSizeMode.Zoom,
                                Location = new Point(num, num2 + autoScrollPosition.Y - 3),
                                BackColor = panelBackgroundColor
                            };

                            Utilities.SetDoubleBuffer(repoIcon, true);
                            modPanel.Controls.Add(repoIcon);

                            repoIcon.BringToFront();

                            num += repoIcon.Width + 8;
                        }
                    }
                    catch (Exception ex) {
                        Console.WriteLine($"[PATCH ERROR] Failed to fetch image variant via reflection: {ex.Message}");
                    }

                    Label repoHeaderLabel = new Label {
                        Name = $"DYNAMIC_REPO_HEADER_{Guid.NewGuid()}",
                        Text = currentRepoName,
                        Font = new Font("Arial", 13f, FontStyle.Bold),
                        ForeColor = Color.White,
                        Location = new Point(num, num2 + autoScrollPosition.Y),
                        AutoSize = true,
                        BackColor = panelBackgroundColor
                    };

                    Utilities.SetDoubleBuffer(repoHeaderLabel, true);
                    modPanel.Controls.Add(repoHeaderLabel);

                    repoHeaderLabel.BringToFront();

                    num2 += repoHeaderLabel.Height + 12;
                    num = 10;

                    for (int i = 0; i < cardsInRepo.Count; i++) {
                        Control cardControl = cardsInRepo[i];
                        int currentItemWidth = (cardControl is DownloadModCard) ? width : cardControl.Width;
                        int currentItemHeight = (cardControl is DownloadModCard) ? height : cardControl.Height;

                        if (num + currentItemWidth > maxPanelRightEdge && num > 10) {
                            num = 10;
                            num2 += currentItemHeight + 3;
                        }

                        cardControl.Location = new Point(num, num2 + autoScrollPosition.Y);
                        num += currentItemWidth + 10;

                        if (i == cardsInRepo.Count - 1) {
                            num2 += currentItemHeight + 15;
                        }
                    }
                }

                modPanel.ResumeLayout();
            });

            return false;
        }
    }
}
