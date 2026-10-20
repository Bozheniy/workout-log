using Microsoft.AspNetCore.Components;

namespace WorkoutLog.Web.Components;

public partial class Icon
{
    [Parameter] public string Name { get; set; } = "";
    [Parameter] public int Size { get; set; } = 22;

    private string PathData => Name switch
    {
        "grid" => "M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z",
        "history" => "M3 11a9 9 0 1 1 2 7 M3 4v7h7 M12 7v5l3 2",
        "settings" => "M4 7h16 M4 17h16 M8 4v6 M16 14v6",
        "check" => "M5 12l4 4L19 6",
        "arrow" => "M5 12h14 M13 6l6 6-6 6",
        "chevron" => "M6 9l6 6 6-6",
        "refresh" => "M20 7v5h-5 M4 17v-5h5 M6 6a8 8 0 0 1 13 3 M18 18a8 8 0 0 1-13-3",
        "cloud" => "M7 18a5 5 0 1 1 0-10 7 7 0 0 1 13 3 4 4 0 0 1-1 7 M12 12v9 M8 17l4 4 4-4",
        "clock" => "M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0 M12 7v5l3 2",
        "dumbbell" => "M7 7v10 M3 9v6 M17 7v10 M21 9v6 M7 12h10 M3 12h4 M17 12h4",
        "download" => "M12 3v12 M7 10l5 5 5-5 M4 16v5h16v-5",
        "link" => "M10 13a5 5 0 0 0 7 0l3-3a5 5 0 0 0-7-7l-2 2 M14 11a5 5 0 0 0-7 0l-3 3a5 5 0 0 0 7 7l2-2",
        "shield" => "M12 3l8 3v6c0 5-8 9-8 9s-8-4-8-9V6z M8 12l3 3 5-6",
        "close" => "M6 6l12 12 M18 6L6 18",
        "play" => "M8 4l12 8-12 8z",
        "wifi" => "M3 8a15 15 0 0 1 18 0 M6 12a10 10 0 0 1 12 0 M9 16a5 5 0 0 1 6 0 M12 20h.01",
        "book" => "M4 3h12a3 3 0 0 1 3 3v15H6a3 3 0 0 1-3-3V5a2 2 0 0 1 2-2 M3 17h16 M7 7h8 M7 11h5",
        _ => "M12 4v16 M4 12h16"
    };
}
