# Callstack Digest

**Turn raw Windows call stacks into clean, LLM‑ready prompts—automatically annotated with the most relevant source snippets.**
Built with .NET 8 (WPF) for Windows.

<img width="851" height="845" alt="image" src="https://github.com/user-attachments/assets/97902512-b766-4ce8-8aff-95063536cd9d" />

---

## Highlights

* **Paste → Prompt in one click**: Paste a call stack, choose a **Mode** (Empty / Explain / Optimize), and copy the generated prompt.
* **Smart source extraction**: Finds the function body for each frame (C++/C/C#), marks the current line with `==>`, and **crops** to a focused window.
* **Prompt size indicator**: Live estimate of clipboard size (UTF‑16) with a tooltip showing UTF‑8 bytes—handy for LLM limits.
* **Per‑mode templates**: Edit and **persist** the prompt template per mode (defaults included for *Explain* and *Optimize*).
* **Practical debugging aids**: A *Sources* tab shows what was found per frame and why.

---

## Why?

When you’re diagnosing an issue (e.g., Unreal Engine networking), you often want to paste a stack into a model and ask “What’s going on?” or “How do I optimize this?”. Raw stacks alone are too vague. **Callstack Digest** pairs the stack with **just enough source** around each top frame to give the model decisive context—without blowing through token limits.

---

## Quick start

### Requirements

* **Windows 10/11**
* **.NET SDK 8.0** (for CLI) or **Visual Studio 2022 17.14+** (WPF workload)

### Build & run

```bash
# CLI
dotnet build -c Release
dotnet run --project CallstackDigest.csproj

# or in Visual Studio: open CallstackDigest.sln and F5
```

---

## Using the app

1. **Paste** your call stack (`Paste` button).
2. Pick a **Mode**:

   * **Empty** – no system prompt; just the inputs.
   * **Explain** – default system prompt aimed at explaining behavior.
   * **Optimize** – default system prompt aimed at performance/reliability analysis.
3. Tune:

   * **Frames to Annotate** – how many **top** frames get source snippets.
   * **Max Function Lines** – max lines per function after cropping.
4. (Optional) Adjust the **prompt template** (per mode) and click **Save** or **Reset**.
5. **Copy** the finished prompt (`Copy` button) and paste into your model/chat tool.
6. Check **Sources** tab to see per-frame extraction details.

You’ll also see **“Size if copied ≈ … KB”** above the prompt, reflecting the UTF‑16 clipboard cost (tooltip shows UTF‑8 as well).

---

## What call stacks look like (examples)

The parser accepts the common two-line format:

```
UnrealEditor-IrisCore.dll!UE::CoreUObject::Private::ResolveObjectHandleNoRead(...) Line 424
    at C:\UnrealEngine\Engine\Source\Runtime\CoreUObject\Public\UObject\ObjectHandle.h(424)

[Inline Frame] UnrealEditor-Mover.dll!operator<<(FArchive &) Line 1777
    at D:\Game\Plugins\Mover\Serialization.cpp(1777)

kernel32.dll!00007ff919167374()
```

It extracts:

* **Module** (e.g., `UnrealEditor-IrisCore.dll`)
* **Symbol** (full name, later simplified for display)
* Optional **file path + line** from the `at … (line)` line

---

## Templates (defaults)

Templates are stored per mode and are editable in the UI. Defaults live in `Config/PromptTemplates.cs`:

* **Explain**

  ```
  System: You are a senior engineer expert in Unreal Engine networking (Iris, NetworkPrediction), C++, and performance.

  Task: Explain what is going on in this callstack.
  For each frame: briefly explain the key points of the algorithm (1–3 bullets).
  Then provide one concise paragraph summarizing the overall behavior.
  ```

* **Optimize**

  ```
  System: You are a senior engineer expert in Unreal Engine networking (Iris, NetworkPrediction), C++, and performance.

  Task: Analyze this callstack for performance and reliability risks.
  Identify hotspots (esp. serialization/replication), redundant work, and contention points.
  Propose concrete improvements (quick wins and deeper changes) with trade-offs.
  ```

Persistence location:
`%APPDATA%\CallstackDigest\templates.json`

---

## Settings & persistence

All settings in the UI are persisted between runs.

---

## Troubleshooting

* **“Source file not found”** (in *Sources* tab)
  The file path from the stack doesn’t exist locally. Fix your workspace layout or customize **RemapPath**.

* **“No frames were parsed”**
  The stack format didn’t match expected patterns. Ensure each relevant frame appears like the examples (module!symbol and an optional `at path(line)` next line).

* **Extraction picked the wrong block**
  This can happen with heavy macros/templates or unusual formatting. Try increasing *Max Function Lines*, or reduce *Frames to Annotate* to focus on top call sites.

---

## Security & privacy

* All processing is **local**.
* The app **reads** source files referenced by your stack; it does not transmit them anywhere.
* No telemetry.

---

## Contributing

Issues and PRs are welcome. Suggested areas:

* Additional call stack formats.
* Better path remapping UX.
* More modes/templates (e.g., *Crash Triage*, *Threading Analysis*).
* Extraction improvements for complex macro/template cases.

Dev environment: Windows 10/11, .NET 8 SDK, VS 2022 17.14+.

---

## License

**No license file is present.** By default, that means *all rights reserved*.
If you’re the repo owner, consider adding a license (e.g., MIT, Apache‑2.0).
