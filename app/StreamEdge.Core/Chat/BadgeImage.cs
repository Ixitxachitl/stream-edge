// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Chat;

/// <summary>A badge to draw before a sender's name.</summary>
/// <param name="ImageUrl">The 2x image (18 px badges drawn at 36 px for HiDPI).</param>
/// <param name="Title">Tooltip text ("Moderator", "6-Month Subscriber", "FFZ Supporter").</param>
/// <param name="BackgroundColor">CSS colour to fill behind the image, for
/// badges designed as a glyph on a coloured tile (FFZ); null for none.</param>
public sealed record BadgeImage(Uri ImageUrl, string Title, string? BackgroundColor = null);
