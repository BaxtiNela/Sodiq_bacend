# Sodiq — Autonomous Desktop AI Agent

> Windows uchun Cursor/Warp darajasida ishlaydigan autonomous AI agent.
> Lokal LLM (llama.cpp / qwen2.5) + ReAct agent loop + 22 ta tool.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![Docker](https://img.shields.io/badge/Docker-required-2496ED)
![License](https://img.shields.io/badge/license-MIT-green)

---

## Nimaga Sodiq?

- **Mustaqil harakat qiladi** — "faylni o'chirish", "veb qidiruv", "screenshot ol" → o'zi bajaradi
- **Internet kiradi** — DuckDuckGo orqali qidiruv, URL o'qish
- **Desktop boshqaradi** — oyna, clipboard, ovoz, screenshot
- **Lokal LLM** — internet kerak emas, GPU bilan tez ishlaydi (RTX 2060+)
- **Integrated Terminal** — PowerShell to'g'ridan-to'g'ri ilovada
- **Kod muharrir** — Monaco (VS Code engine) bilan fayl tahrirlash

---

## Arxitektura

```
WPF App (WA.App)           ← Foydalanuvchi interfeysi
  ↕ SignalR WebSocket
ASP.NET Core (WA.Backend)  ← Agent loop, context builder, xotira
  ↕ HTTP /v1/chat/completions
llama.cpp server (Docker)  ← qwen2.5-7b yoki deepseek-r1:8b
```

> **Tool execution WPF tomonda ishlaydi** — Docker CMD bajara olmaydi.
> Backend tool call so'rovini WPF ga yuboradi → WPF bajaradi → natijani qaytaradi.

---

## Toollar (22 ta)

| Kategoriya | Toollar |
|-----------|---------|
| **Tizim** | `run_command`, `run_powershell`, `get_system_info`, `get_time`, `get_env` |
| **Fayl** | `read_file`, `write_file`, `list_directory`, `search_files`, `delete_file`, `rename_file` |
| **Desktop** | `take_screenshot`, `get_clipboard`, `set_clipboard`, `list_windows`, `focus_window`, `close_window`, `set_volume`, `open_app` |
| **Internet** | `web_search` (DuckDuckGo+Bing), `read_url` |
| **Xotira** | `save_memory`, `recall_memory` |

---

## Tezkor boshlash

### Talab
- Windows 10/11 (x64)
- Docker Desktop (GPU support: NVIDIA RTX tavsiya etiladi)
- .NET 8 SDK (build uchun)

### 1. LLM model

```bash
# Modelni yuklab olish (~4.5 GB)
docker run --rm -v "%USERPROFILE%\.ollama:/root/.ollama" ollama/ollama pull qwen2.5:7b
```

### 2. Backend + LLM server

```bash
git clone https://github.com/BaxtiNela/AISDTUDIO.git
cd AISDTUDIO
docker compose up -d
```

Healthcheck: `wa-llm` tayyor bo'lgandan keyin `wa-backend` avtomatik boshlanadi.

### 3. WPF dasturini build qilish

```bash
dotnet publish src/WA.App -c Release -r win-x64 --self-contained true -o publish/
publish\WindowsAssistantNext.exe
```

---

## Integrated Terminal

Dastur ichida PowerShell terminal:
- `>_` tugmasi yoki `Ctrl+`` — ochish/yopish
- Agent `run_command`/`run_powershell` chaqirsa → natija terminalda ko'rinadi
- `↑`/`↓` — buyruqlar tarixi

---

## Agent Loop (ReAct)

```
Foydalanuvchi xabar
  → System prompt (ReAct: Tahlil → Harakat → Kuzat → Yakun)
  → LLM (qwen2.5-7b, max 15 round)
    → Tool call → WPF bajaradi → natija LLM ga
    → LLM keyingi qadam yoki yakuniy javob
  → StreamToken (real-time) → FinalResponse
```

---

## Konfiguratsiya

`docker-compose.yml` yoki environment:

| Variable | Default | Tavsif |
|---------|---------|--------|
| `LLM_URL` | `http://llama-server:8080` | llama.cpp URL |
| `OLLAMA_URL` | `http://localhost:11434` | Ollama fallback |
| `ConnectionStrings__DefaultConnection` | `/data/assistant.db` | SQLite DB yo'li |

Model fayllar: `%USERPROFILE%\.ollama` → Docker `/root/.ollama`

---

## Proyekt tuzilishi

```
src/
  WA.App/          — WPF UI (chat, fayl daraxti, Monaco editor, terminal)
  WA.Agent/        — Tool executor library (CMD, PS, fayl, desktop, web)
  WA.Backend/      — ASP.NET Core: SignalR hub, LLM client, xotira, context
    Hubs/          — AssistantHub.cs (SignalR, agent loop)
    Services/      — OllamaBackendClient, ContextBuilder, Memory, Learning
docker-compose.yml
Dockerfile         — wa-backend
Dockerfile.llm     — llama-cpp-python server (GPU)
```

---

## Litsenziya

MIT © 2025 BaxtiNela
