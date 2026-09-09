# mww_mod_launcher_k_knight_repo

A simple patcher for the Magicka Wizard Wars mod launcher that overrides mod repository URLs using Mono.Cecil and Harmony.
<div>
  <h3>
    <a href="https://github.com/k-Knight/mww_mod_launcher_k_knight_repo/releases/latest/download/MWW_MOD_LAUNCHER_PATCHER.exe">
      📦 <code>[ DOWNLOAD THE PATCHER ]</code>
    </a>
  </h3>
</div>

## What it does
* Patches the default launcher to inject custom mod repository definitions.
* Swaps out the official URLs so you can use alternative or self-hosted mod listings.

## Project Structure
* `exe_patcher/` - The console app that applies the file modifications.
* `k_knight_mod_repo/` - The code containing the repository overrides and Harmony hooks.
* `resources/` - Base files used for patching.

## How to build
1. Open `mww_mod_launcher_k_knight_repo.sln` in Visual Studio.
2. Build the solution in **Release** mode.
3. Find the output files in `bin/Release/`.
