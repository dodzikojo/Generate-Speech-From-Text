## Produce a flat dataset

@action: Repeat the export with CSV and the same filtered scope and key columns.

CSV is best for importing into another data pipeline because it removes workbook formatting and multiple-sheet presentation.

Custom element fields become columns, and complex history is handled through the export's supported packaging rather than embedded spreadsheet formatting.

## Check machine-readable trade-offs

@screen: Open the CSV in a plain table view and compare it with Excel.

Colours, charts, images, formulas, and rich layout are not part of CSV. Dates, units, delimiters, and text encoding matter to the receiving system.

Verify the row count and column headers before automating downstream use. For a durable integration, prefer the API over repeatedly parsing a manually configured export.
