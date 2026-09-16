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

namespace Lockwell.Mobile.Services;

/// <summary>
/// Reaches the DI container from pages that are constructed directly rather than
/// resolved. Small enough not to justify wiring every page through the container.
/// </summary>
public static class ServiceHelper
{
    public static T? GetService<T>() =>
        Current is null ? default : Current.GetService<T>();

    private static IServiceProvider? Current => IPlatformApplication.Current?.Services;
}
