// <copyright file="ClassExtensions.cs" company="MTVirux">
// Copyright (c) MTVirux. All rights reserved.
// </copyright>

namespace MarketTerror.Extensions
{
  using Lumina.Excel;
  using Lumina.Excel.Sheets;

  /// <summary>
  /// <see cref="ClassJobCategory"/> and <see cref="ClassJob"/> extensions.
  /// </summary>
  public static class ClassExtensions
  {
    /// <summary>
    /// Checks if <see cref="ClassJobCategory"/> contains <see cref="ClassJob"/>.
    /// </summary>
    /// <param name="classJobCategory">A <see cref="ClassJobCategory"/>.</param>
    /// <param name="classJob">A <see cref="ClassJob"/>.</param>
    /// <returns>
    /// True if contained or classJob is null.
    /// False if not contained.
    /// </returns>
    public static bool HasClass(this ClassJobCategory classJobCategory, ClassJob? classJob)
    {
      if (!classJob.HasValue)
      {
        return true;
      }

      var row = new RawRow(classJobCategory.ExcelPage, classJobCategory.RowOffset, classJobCategory.RowId);

      // Column 0 is the category name, followed by one bool column per class job in row id order.
      var column = (int)classJob.Value.RowId + 1;

      return column < row.Columns.Count && row.ReadBoolColumn(column);
    }
  }
}
