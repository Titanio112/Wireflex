namespace Wireflex.Core.Adb;

public sealed class AdbException : Exception
{
    public AdbException(string message)
        : base(message)
    {
    }

    public AdbException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
