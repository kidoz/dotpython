namespace DotPython.Protocol;

public readonly record struct WorkerProtocolVersion(int Major, int Minor)
    : IComparable<WorkerProtocolVersion>
{
    public static WorkerProtocolVersion Current { get; } = new(2, 0);

    public int CompareTo(WorkerProtocolVersion other)
    {
        var major = Major.CompareTo(other.Major);
        return major != 0 ? major : Minor.CompareTo(other.Minor);
    }

    public static bool operator <(WorkerProtocolVersion left, WorkerProtocolVersion right) =>
        left.CompareTo(right) < 0;

    public static bool operator <=(WorkerProtocolVersion left, WorkerProtocolVersion right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >(WorkerProtocolVersion left, WorkerProtocolVersion right) =>
        left.CompareTo(right) > 0;

    public static bool operator >=(WorkerProtocolVersion left, WorkerProtocolVersion right) =>
        left.CompareTo(right) >= 0;

    public override string ToString() => $"{Major}.{Minor}";
}
