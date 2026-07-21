namespace DotPython.Worker;

public enum WorkerProcessState
{
    Starting,
    Running,
    Draining,
    Stopped,
    Faulted,
}
