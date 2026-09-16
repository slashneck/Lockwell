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

namespace Lockwell.Services;

/// <summary>Progress report for long-running vault operations shown in the UI.</summary>
public sealed class OperationProgress
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public int Current { get; init; }
    public int Total { get; init; }
    public bool IsIndeterminate { get; init; }
}
