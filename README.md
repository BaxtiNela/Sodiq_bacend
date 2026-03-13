# Sodiq Studio — AI Windows Assistant

**Sodiq Studio** — Windows uchun avtonom AI agent. Groq cloud API (Llama 4 Scout) orqali ishlaydi, kompyuterni mustaqil boshqaradi va foydalanuvchi bilan suhbatlashadi.

---

## Arxitektura

```text
WPF App (Sodiq.exe)
    │
    ├── SignalR ──→ WA.Backend (Docker :5000)
    │                   │
    │                   └── Groq API (Llama 4 Scout / 70B)
    │                         └── fallback: DeepSeek / GPT-4o
    │
    └── ToolExecutor (lokal: CMD, PowerShell, fayl ops)
```

| Qism | Texnologiya | Vazifa |
| --- | --- | --- |
| **WA.App** | WPF / .NET 8 | UI, chat, fayl daraxti, editor, tray |
| **WA.Backend** | ASP.NET Core + SignalR | Agent loop, LLM so'rovlar, kontekst |
| **WA.Agent** | .NET 8 lib | ToolExecutor — 24+ tool |
| **LLM** | Groq Cloud | Llama 4 Scout 17B (vision + tools) |

---

## Xususiyatlar

- **Agent loop** — tool call sikli (max 15 qadam), fallback zanjiri
- **24+ tool** — `open_app`, `run_command`, `run_powershell`, `web_search`, `read_file`, `write_file`, `take_screenshot`, `set_volume` va boshqalar
- **Ovoz bilan buyruq** — Groq Whisper STT (O'zbek tili)
- **Tizim tray** — minimizatsiyada mini widget qoladi
- **Monaco Editor** — fayl ko'rish va tahrirlash
- **Session tabs** — bir nechta parallel suhbat
- **Memory** — `LearningService` foydalanuvchi odatlari va faktlarni saqlaydi
- **AgentResponseBubble** — Claude Code uslubida collapsible tool steps

---

## O'rnatish

### Talablar

- Docker Desktop
- .NET 8 SDK
- Windows 10/11

### 1. `.env` fayl yaratish

```bash
cp .env.example .env
# GROQ_API_KEY ni to'ldiring
```

`.env.example` tarkibi:

```env
GROQ_API_KEY=gsk_...
DEEPSEEK_API_KEY=
OPENAI_API_KEY=
```

### 2. Backend ishga tushirish

```bash
docker compose up -d
```

Backend: `http://localhost:5000`

### 3. WPF ilovani build qilish

```bash
dotnet publish src/WA.App -c Release -r win-x64 --self-contained true -o publish_out
```

---

## Groq modellari

| Model | Tezlik | Qobiliyat |
| --- | --- | --- |
| `meta-llama/llama-4-scout-17b-16e-instruct` | Juda tez | Vision + Tools |
| `llama-3.3-70b-versatile` | Tez | Kuchli fikrlash |
| `deepseek-chat` | O'rtacha | Kod yozish |

Fallback zanjiri: Scout → 70B → DeepSeek (rate limit bo'lsa avtomatik)

---

## Agent qoidalari

- Salomlashish / tushuntirish → to'g'ridan javob (tool yo'q)
- Task (fayl, app, terminal) → darhol tool call (oldin matn yozmaslik)
- Bitta tool — bitta qadam
- Tool natijasidan keyin → keyingi tool yoki yakuniy javob
- Bir xil muvaffaqiyatsiz toolni takrorlamaslik

---

## CI/CD

GitHub Actions orqali har `main` ga push qilinganda `wa-backend` Docker image build bo'lib `ghcr.io` ga push qilinadi.

Kerakli secret:

- `GITHUB_TOKEN` — GitHub avtomatik beradi (qo'shimcha sozlash kerak emas)

Workflow fayli: [`.github/workflows/docker-build.yml`](.github/workflows/docker-build.yml)

---

## Litsenziya

MIT
