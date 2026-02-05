using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace client.Models;

public class ChatMessageItem : INotifyPropertyChanged
{
    private string _sender = "";
    private string _text = "";
    private bool _isMine;

    public string Sender
    {
        get => _sender;
        set { _sender = value ?? ""; OnPropertyChanged(); }
    }

    public string Text
    {
        get => _text;
        set { _text = value ?? ""; OnPropertyChanged(); }
    }

    public bool IsMine
    {
        get => _isMine;
        set { _isMine = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
