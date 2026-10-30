namespace Damebooru.Core.Entities;

public enum LibraryKind
{
    Library = 0,
    // Unsorted files waiting to be moved into a library; no folder tags, hidden from search unless asked for.
    Inbox = 1
}
