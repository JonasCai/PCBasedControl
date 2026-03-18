namespace UI.ViewModels;

public class PagePlaceholderViewModel
{
    public PagePlaceholderViewModel(string title, string description)
    {
        Title = title;
        Description = description;
    }

    public string Title { get; }

    public string Description { get; }
}
