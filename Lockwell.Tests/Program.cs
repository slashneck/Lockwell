// Lockwell - local-only encrypted vault
// Copyright (C) 2026 Lockwell
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
// FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using Lockwell.Tests;

// The verification harness for Lockwell.
//
// Every check runs against a throwaway vault in its own temp folder. The real vault in
// %LocalAppData%\Lockwell is never opened, read or written by anything in here.
//
//   dotnet run --project Lockwell.Tests

Console.WriteLine("Lockwell verification harness");
Console.WriteLine("Throwaway vaults only. The real vault is never touched.");

VaultChecks.Run();
LogicChecks.Run();
CompressionChecks.Run();
SyncChecks.Run();
await HandshakeChecks.RunAsync();
await LinkingChecks.RunAsync();
await TransferChecks.RunAsync();
await RoundTripChecks.RunAsync();
NetworkChecks.Run();
FolderChecks.Run();
HardeningChecks.Run();
await DiscoveryChecks.RunAsync();
ResourceChecks.Run();
EffectChecks.Run();
UpdateChecks.Run();

Console.WriteLine();
if (Check.Failures == 0)
{
    Console.WriteLine("ALL CHECKS PASSED");
    return 0;
}

Console.WriteLine($"{Check.Failures} CHECK(S) FAILED");
return 1;
