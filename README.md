# Sodiq — Autonomous Desktop AI Agent

> Windows uchun Cursor/Warp darajasida ishlaydigan autonomous AI agent.
> Lokal LLM (llama.cpp / qwen2.5) + ReAct agent loop + 22 ta tool.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![Docker](https://img.shields.io/badge/Docker-required-2496ED)
![License](https://img.shields.io/badge/license-MIT-green)

---

## Nimaga Sodiq?

- **Mustaqil harakat qiladi** — "skrinshot ol", "veb qidiruv", "faylni o'chirish" → o'zi bajaradi
- **Internet kiradi** — DuckDuckGo + Bing orqali qidiruv, URL mazmunini o'qish
- **Desktop boshqaradi** — oyna, clipboard, ovoz, screenshot, dastur ochish
- **Lokal LLM** — internet kerak emas, GPU bilan tez ishlaydi (RTX 2060+)
- **Integrated Terminal** — PowerShell to'g'ridan-to'g'ri ilovada (`Ctrl+\``)
- **Kod muharrir** — Monaco (VS Code engine) bilan fayl tahrirlash
- **Ko'p fayl konteksti** — bir vaqtda 5 ta fayl AI ga yuborish

---

## Arxitektura

```
┌─────────────────────────────────────────────────────────┐
│  WPF App (WA.App)  — UI: chat, fayl daraxti, terminal  │
│    ↕ SignalR WebSocket (localhost:5000/hub/assistant)   │
│  ASP.NET Core (WA.Backend, Docker)                      │
│    — Agent loop, context builder, LLM client, xotira   │
│    ↕ HTTP /v1/chat/completions                          │
│  llama.cpp server (Docker, port 8080)                   │
│    — qwen2.5-7b yoki deepseek-r1:8b (GPU)              │
└─────────────────────────────────────────────────────────┘
```

> **Tool execution WPF tomonda ishlaydi** — Docker ichida CMD/PS bajara olmaydi.
> Backend `ToolCallRequest` yuboradi → WPF bajaradi → `SendToolResult` qaytaradi.

---

## Proyekt tuzilishi

```
src/
  WA.App/                        ← WPF UI (net8.0-windows)
    Views/MainWindow.xaml        ← Asosiy oyna XAML
    Views/MainWindow.xaml.cs     ← UI logic, SignalR client, terminal
    Controls/ChatBubble.cs       ← AI/user xabar komponenti
    Controls/ToolActivityBubble  ← Tool bajarilish animatsiyasi
    Services/UserProfileStore.cs ← Foydalanuvchi profili (JSON)
    Resources/sodiq_logo.png     ← Ilova ikonkasi

  WA.Agent/                      ← Tool executor library (net8.0-windows)
    Services/ToolExecutor.cs     ← 22 ta tool implementatsiyasi
    Services/WebSearchService.cs ← DuckDuckGo + Bing + URL reader (YANGI)
    Services/OllamaClient.cs     ← Lokal agent HTTP client
    Services/MemoryService.cs    ← SQLite xotira
    WA.Agent.csproj              ← UseWPF=true (clipboard uchun)

  WA.Backend/                    ← ASP.NET Core (Docker)
    Hubs/AssistantHub.cs         ← SignalR hub, agent loop (15 round)
    Services/OllamaBackendClient ← LLM HTTP client, system prompt
    Services/ContextBuilder      ← Foydalanuvchi profili + xotira → prompt
    Services/LearningService     ← Suhbatdan faktlarni avtomatik chiqarish
    Services/MemoryBackendService← Semantik xotira qidirish

docker-compose.yml               ← llama-server + wa-backend
Dockerfile                       ← wa-backend (ASP.NET Core)
Dockerfile.llm                   ← llama-cpp-python (GPU)
README.md                        ← Ushbu hujjat
```

---

## Barcha Toollar (22 ta)

### Tizim buyruqlari

| Tool | Tavsif |
|------|--------|
| `run_command` | Windows CMD buyrug'ini bajarish |
| `run_powershell` | PowerShell skriptini bajarish |
| `get_system_info` | CPU, RAM, disk, OS ma'lumotlari |
| `get_time` | Hozirgi vaqt, sana, timezone |
| `get_env` | Environment o'zgaruvchisini o'qish |

### Fayl operatsiyalari

| Tool | Tavsif |
|------|--------|
| `read_file` | Fayl mazmunini o'qish |
| `write_file` | Faylga yozish yoki yangi fayl yaratish |
| `list_directory` | Papka tarkibini ko'rish |
| `search_files` | `*.py`, `*.txt` kabi pattern bilan qidirish |
| `delete_file` | Faylni o'chirish (system32 himoyalangan) |
| `rename_file` | Faylni ko'chirish yoki qayta nomlash |

### Desktop boshqaruv

| Tool | Tavsif |
|------|--------|
| `take_screenshot` | Ekran rasmini PNG sifatida saqlash |
| `get_clipboard` | Clipboard mazmunini o'qish |
| `set_clipboard` | Clipboardga matn yozish |
| `list_windows` | Hozir ochiq oynalar ro'yxati |
| `focus_window` | Oynani oldinga chiqarish (Win32) |
| `close_window` | Oynani yopish (explorer himoyalangan) |
| `set_volume` | Tizim ovozini o'rnatish (0–100%) |
| `open_app` | Dastur ochish |

### Internet

| Tool | Tavsif |
|------|--------|
| `web_search` | DuckDuckGo Lite HTML scraping + Bing fallback |
| `read_url` | URL sahifasini o'qib, matnni chiqarish |

### Xotira

| Tool | Tavsif |
|------|--------|
| `save_memory` | Muhim ma'lumotni doimiy xotiraga saqlash |
| `recall_memory` | Xotiradan kalit so'z bilan qidirish |

---

## Agent Loop — ReAct

```
Foydalanuvchi xabar
  ↓
System prompt: ReAct (Tahlil → Harakat → Kuzat → Yakun)
  ↓
LLM: qwen2.5-7b | max 15 round
  ├─ Tool call kerak?
  │    ├─ ToolCallRequest → WPF (ToolExecutor)
  │    ├─ WPF bajaradi (CMD/PS/fayl/internet/desktop)
  │    ├─ SendToolResult → Backend
  │    └─ LLM natijani ko'radi → keyingi qadam
  └─ Javob tayyor?
       ├─ StreamToken (real-time oqim)
       └─ FinalResponse → DB ga saqlash → Learning
```

**System prompt (ReAct uslubi):**
```
FIKRLASH TARTIBI:
1. TAHLIL  — Vazifani tushun, kerakli toolni tanla
2. HARAKAT — Tool ishlatib vazifani boshlash
3. KUZAT   — Natijani ko'r, xato bo'lsa tuzat
4. YAKUN   — Foydalanuvchiga faqat natijani yoz

QATTIQ QOIDALAR:
- HECH QACHON "siz qiling", "bosing" dema — O'ZING qil
- Xato bo'lsa muqobil yo'l bil, baribir natija chiqar
- Internet kerakmi → web_search → read_url ketma-ket ishlat
```

**LLM parametrlari:**
```
temperature:      0.75   (ijodiy, lekin aniq)
repeat_penalty:   1.15   (takrorlashni kamaytiradi)
top_p:            0.92
frequency_penalty: 0.1
max_tokens:       2048
```

---

## Integrated PowerShell Terminal

Ilova ichida to'liq ishlayotgan terminal:

- **Ochish:** `>_` tugmasi (activity bar) yoki `Ctrl+\``
- **Yopish:** `✕` tugmasi yoki yana `Ctrl+\``
- **Tozalash:** `🗑` tugmasi
- **Buyruqlar tarixi:** `↑` / `↓`
- **Resize:** Splitter bilan balandlikni o'zgartirish

**Agent integratsiyasi:**
Agent `run_command` yoki `run_powershell` chaqirsa, natija avtomatik terminalda `[Agent → run_powershell]` ko'rinishida paydo bo'ladi.

**Qo'llab-quvvatlanadigan tillar (terminal orqali):**
```powershell
python script.py        # Python
node index.js           # Node.js
dotnet run              # C# / .NET
cargo run               # Rust
go run main.go          # Go
java -jar app.jar       # Java
git status              # Git
```

---

## Ko'p Fayl Konteksti (Multi-file)

- Fayl daraxtidan fayl bosing → chip paydo bo'ladi
- `📎` tugmasi → fayl dialog (bir vaqtda bir nechta)
- Maksimal 5 ta fayl
- Har bir chip `✕` bilan o'chiriladi
- Xabar yuborilganda barcha fayllar mazmuni AI ga qo'shiladi

---

## Docker Sozlamalari

### docker-compose.yml

```yaml
services:
  llama-server:       # llama-cpp-python (GPU), port 8080
    healthcheck:
      test: python -c "import urllib.request; urllib.request.urlopen('http://localhost:8080/v1/models')"
      interval: 30s
      retries: 5
      start_period: 60s

  wa-backend:         # ASP.NET Core, port 5000
    depends_on:
      llama-server:
        condition: service_healthy  # LLM tayyor bo'lgandan keyin boshlanadi
```

**Muhim:** `curl` llama-cpp-python containerida yo'q — `python urllib` ishlatiladi.

### Konfiguratsiya

| Environment | Default | Tavsif |
|-------------|---------|--------|
| `LLM_URL` | `http://llama-server:8080` | llama.cpp server |
| `OLLAMA_URL` | `http://localhost:11434` | Ollama fallback |
| `ConnectionStrings__DefaultConnection` | `/data/assistant.db` | SQLite DB |

Model fayllar: `%USERPROFILE%\.ollama` → Docker `/root/.ollama`

---

## Tezkor Boshlash

### 1. Model yuklab olish

```bash
docker run --rm -v "%USERPROFILE%\.ollama:/root/.ollama" ollama/ollama pull qwen2.5:7b
```

### 2. Backend ishga tushirish

```bash
git clone https://github.com/BaxtiNela/AISDTUDIO.git
cd AISDTUDIO
docker compose up -d
```

`wa-llm` → `Healthy` → `wa-backend` avtomatik boshlanadi.

### 3. WPF App

```bash
# Build + publish
dotnet publish src/WA.App -c Release -r win-x64 --self-contained true -o publish/

# Ishga tushirish
publish\WindowsAssistantNext.exe
```

---

## Versiya Tarixi

### v2.0 — Autonomous Agent (2026-03-12)

**Yangi funksiyalar:**

- ✅ **ReAct system prompt** — Tahlil→Harakat→Kuzat→Yakun
- ✅ **22 ta tool** (oldin 10 ta edi)
  - Yangi: `take_screenshot`, `get_clipboard`, `set_clipboard`
  - Yangi: `list_windows`, `focus_window`, `close_window`, `set_volume`
  - Yangi: `delete_file`, `rename_file`, `get_time`, `get_env`
  - Yangi: `web_search`, `read_url` (internet!)
- ✅ **WebSearchService** — DuckDuckGo Lite HTML scraping + Bing fallback
- ✅ **Integrated PowerShell terminal** — `Ctrl+\``, agent mirroring
- ✅ **Multi-file context** — 5 ta fayl bir vaqtda, chip UI
- ✅ **File tree collapsed** — bosib ochiladigan, lazy-load
- ✅ **Docker healthcheck** — `python urllib` (curl yo'q)
- ✅ **LLM parametrlar** — temperature, repeat_penalty, top_p tuning
- ✅ **WA.Agent: UseWPF** — clipboard API uchun

**Tuzatilgan xatolar:**

- `tool_call_id` missing → 500 error (llama-cpp-python bilan)
- ToolExecutor dead code (`simpleScript`) compile error
- `System.IO` implicit usings UseWPF bilan yo'qolishi

### v1.0 — Initial Release

- WPF + ASP.NET Core + llama.cpp arxitekturasi
- 10 ta tool (CMD, PS, fayl, app, xotira)
- SignalR bidirectional hub
- Monaco kod muharriri
- LearningService (suhbatdan faktlar)

---

## Litsenziya

MIT © 2025 BaxtiNela
