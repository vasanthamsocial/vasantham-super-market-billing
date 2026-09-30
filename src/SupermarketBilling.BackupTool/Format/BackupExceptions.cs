namespace SupermarketBilling.BackupTool.Format;

/// <summary>Base type for expected backup failures that are reported to the operator without a stack trace.</summary>
internal class BackupException : Exception
{
    public BackupException(string message)
        : base(message)
    {
    }

    public BackupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The file is not a valid backup, or it has been corrupted, truncated or modified.</summary>
internal sealed class BackupIntegrityException : BackupException
{
    public BackupIntegrityException(string message)
        : base(message)
    {
    }

    public BackupIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The supplied passphrase does not match the one used to create the backup.</summary>
internal sealed class BackupPassphraseException : BackupException
{
    public BackupPassphraseException(string message)
        : base(message)
    {
    }
}
