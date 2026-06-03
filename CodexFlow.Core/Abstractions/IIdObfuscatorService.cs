using System;

namespace CodexFlow.Core.Abstractions;

public interface IIdObfuscatorService
{
    string Encode(long id);
    long Decode(string encoded);
}
