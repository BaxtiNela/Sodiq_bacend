namespace WA.Backend.Models;

public class Habit
{
    public int Id { get; set; }

    /// <summary>Odatning tavsifi, masalan: "Ertalab vazifalarni tekshiradi"</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Necha marta kuzatilgan</summary>
    public int Frequency { get; set; } = 1;

    /// <summary>Qaysi vaqtda yuz beradi: morning, afternoon, evening, anytime</summary>
    public string TimeOfDay { get; set; } = "anytime";

    /// <summary>Qo'shimcha kontekst</summary>
    public string Context { get; set; } = string.Empty;

    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}
