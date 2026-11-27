namespace Lotus.Core
{
    /// <summary>Switches removable storage between writable and read-only.</summary>
    public interface IReadOnlyPolicy
    {
        bool IsApplied();
        void Apply();
        void Remove();
    }
}
