namespace KeList.Models;

public sealed class AppData
{
    public AppSettings Settings { get; set; } = new();
    public List<TodoItem> Items { get; set; } = [];
}
