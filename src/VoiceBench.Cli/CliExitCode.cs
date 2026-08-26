namespace IssueLab.VoiceBench.Cli;

public enum CliExitCode
{
    Success = 0,
    UnexpectedFailure = 1,
    Usage = 2,
    ValidationFailure = 10,
    CredentialFailure = 20,
    ProviderFailure = 30,
    MasteringFailure = 40,
}
