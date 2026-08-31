#region Using directives

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;

#endregion

public class ImportAndExportTranslations : BaseNetLogic
{
    /// <summary>
    /// Exports the translations from the localization dictionary to a CSV file.
    /// </summary>
    /// <remarks>
    /// This method retrieves the CSV file path, character separator, and wrap fields settings from the logic object.
    /// It then accesses the localization dictionary and writes its contents to the specified CSV file.
    /// If any errors occur during the process, they are logged to the output.
    /// </remarks>
    [ExportMethod]
    public void ExportTranslations()
    {
        Log.Info("ImportAndExportTranslations.Export", "Exporting dictionary to CSV file");
        string csvPath = GetCSVFilePath();
        if (string.IsNullOrEmpty(csvPath))
        {
            Log.Error("ImportAndExportTranslations.Export", "An error was detected while reading the CSV file path, check Studio Output for more details");
            return;
        }

        char? characterSeparator = GetCharacterSeparator();
        if (characterSeparator == null || characterSeparator == '\0')
            return;

        bool wrapFields = GetWrapFields();

        var localizationDictionary = GetDictionary();
        if (localizationDictionary == null)
        {
            Log.Error("ImportAndExportTranslations.Export", "No valid LocalizationDictionary was found");
            return;
        }

        string[,] dictionary = (string[,])localizationDictionary.Value.Value;
        int rowCount = dictionary.GetLength(0);
        int columnCount = dictionary.GetLength(1);

        try
        {
            using (var csvWriter = new CsvFileWriter(csvPath) { FieldDelimiter = characterSeparator.Value, WrapFields = wrapFields })
            {
                for (int currentRow = 0; currentRow < rowCount; ++currentRow)
                {
                    string[] row = new string[columnCount];

                    for (int currentColumn = 0; currentColumn < columnCount; ++currentColumn)
                    {
                        if (currentRow == 0 && currentColumn == 0)
                            row[currentColumn] = "Key";
                        else
                            row[currentColumn] = ReplaceNewLineWithSymbol(dictionary[currentRow, currentColumn] ?? string.Empty);
                    }

                    csvWriter.WriteLine(row);
                }
            }

            Log.Info("ImportAndExportTranslations.Export", $"Translations successfully exported to \"{csvPath}\"");
        }
        catch (Exception ex)
        {
            Log.Error("ImportAndExportTranslations.Export", $"Unable to export the translations: {ex}");
        }
    }

    /// <summary>
    /// Imports translations from a CSV file into the localization dictionary.
    /// </summary>
    /// <remarks>
    /// This method retrieves the CSV file path, character separator, and wrap fields settings from the logic object.
    /// It then accesses the localization dictionary and reads the contents of the specified CSV file.
    /// If any errors occur during the process, they are logged to the output.
    /// </remarks>
    [ExportMethod]
    public void ImportTranslations()
    {
        Log.Info("ImportAndExportTranslations.Import", "Importing translations from CSV file");
        string csvPath = GetCSVFilePath();

        if (string.IsNullOrEmpty(csvPath))
        {
            Log.Error("ImportAndExportTranslations.Import", "An error was detected while reading the CSV file path, check Studio Output for more details");
            return;
        }

        char? characterSeparator = GetCharacterSeparator();
        if (characterSeparator == null || characterSeparator == '\0')
            return;

        bool wrapFields = GetWrapFields();

        var localizationDictionary = GetDictionary();
        if (localizationDictionary == null)
        {
            Log.Error("ImportAndExportTranslations.Import", "No valid LocalizationDictionary was found!");
            return;
        }

        if (!File.Exists(csvPath))
        {
            Log.Error("ImportAndExportTranslations.Import", $"The file at '{csvPath}' does not exist");
            return;
        }

        try
        {
            using var csvReader = new CsvFileReader(csvPath) { FieldDelimiter = characterSeparator.Value, WrapFields = wrapFields };
            if (csvReader.EndOfFile())
            {
                Log.Error("ImportAndExportTranslations.Import", $"The file at \"{csvPath}\" is empty");
                return;
            }

            var fileTranslations = csvReader.ReadAll();
            if (fileTranslations.Count == 0 || fileTranslations[0].Count == 0)
                return;

            // Check if any empty column exists in the CSV file
            if (fileTranslations[0].Exists(field => string.IsNullOrEmpty(field)))
            {
                Log.Error("ImportAndExportTranslations.Import", "One or more empty language columns found into CSV file");
                return;
            }

            // Check if first cell contains: "Key"
            if (fileTranslations[0][0] != "Key")
            {
                Log.Error("ImportAndExportTranslations.Import", "The first column header is not 'Key'");
                return;
            }

            // Remaining cells must contain ISO locale (e.g., "en-US", "fr-FR", etc.)
            if (fileTranslations[0].Skip(1).Any(header => !localizationsRegex.IsMatch(header)))
            {
                Log.Error("ImportAndExportTranslations.Import", "One or more columns have incorrect header format into CSV file (expected ISO 639-1 language code and ISO 3166-1 country code like 'en-US')");
                return;
            }

            // All rows must have the same number of columns
            int expectedColumnCount = fileTranslations[0].Count;
            if (fileTranslations.Exists(row => row.Count != expectedColumnCount))
            {
                Log.Error("ImportAndExportTranslations.Import", "One or more rows have incorrect number of columns into CSV file");
                return;
            }

            // Get dimensions of both file translations and actual translations
            int fileTranslationsRows = fileTranslations.Count;
            int fileTranslationsColumns = fileTranslations[0].Count;

            // Get actual translations from the localization dictionary
            string[,] currentDictionaryContent = (string[,])localizationDictionary.Value.Value;
            int actualTranslationsRows = currentDictionaryContent.GetLength(0);
            int actualTranslationsColumns = currentDictionaryContent.GetLength(1);

            string[,] newTranslations;

            // Check for column mismatch
            if (actualTranslationsColumns > fileTranslationsColumns)
            {
                Log.Error("ImportAndExportTranslations.Import", "One or more columns are missing into CSV file");
                return;
            }

            // Prepare new translations array with appropriate dimensions
            if (actualTranslationsColumns < fileTranslationsColumns)
            {
                newTranslations = new string[actualTranslationsRows, fileTranslationsColumns];
            }
            else
            {
                newTranslations = new string[actualTranslationsRows, actualTranslationsColumns];
            }

            // Check for removed keys
            var currentTranslationsList = currentDictionaryContent.Cast<string>()
                .Select((cellValue, cellIndex) => new { cellValue, rowIndex = cellIndex / currentDictionaryContent.GetLength(1) })
                .GroupBy(cell => cell.rowIndex)
                .Select(rowGroup => rowGroup.Select(cell => cell.cellValue).ToList())
                .Skip(1)
                .ToList();

            // Get keys from file translations
            var fileTranslationKeys = fileTranslations.Select(fileTranslation => fileTranslation.FirstOrDefault()).Skip(1).ToList();
            var removedTranslations = fileTranslationKeys.Except(currentTranslationsList.Select(actualTranslation => actualTranslation.FirstOrDefault())).ToList();

            // Add space for new keys if any
            if (removedTranslations.Count > 0)
            {
                string[,] newTranslationsResized = new string[actualTranslationsRows + removedTranslations.Count, Math.Max(actualTranslationsColumns, fileTranslationsColumns)];
                Array.Copy(newTranslations, newTranslationsResized, newTranslations.Length);
                newTranslations = newTranslationsResized;

                int newKeysIndex = 0;
                foreach (string removedTranslation in removedTranslations)
                {
                    for (int keyFoundColumn = 0; keyFoundColumn < Math.Max(actualTranslationsColumns, fileTranslationsColumns); ++keyFoundColumn)
                    {
                        newTranslations[actualTranslationsRows + newKeysIndex, keyFoundColumn] = ReplaceSymbolWithNewLine(fileTranslations[fileTranslationKeys.IndexOf(removedTranslation) + 1][keyFoundColumn]);
                    }

                    newKeysIndex++;
                }
            }

            fileTranslations[0][0] = "";

            long originalSize = GetDictionarySize(localizationDictionary);
            int keyFoundRow;
            int keyUpdated = 0;

            // Update existing keys and add new keys
            for (int currentActualTranslationRow = 0; currentActualTranslationRow < actualTranslationsRows; ++currentActualTranslationRow)
            {
                keyFoundRow = -1;
                for (int currentFileTranslationRow = 0; currentFileTranslationRow < fileTranslationsRows; ++currentFileTranslationRow)
                {
                    if (currentDictionaryContent[currentActualTranslationRow, 0] == fileTranslations[currentFileTranslationRow][0])
                    {
                        keyFoundRow = currentFileTranslationRow;
                        break;
                    }
                }

                if (keyFoundRow >= 0)
                {
                    for (int keyFoundColumn = 0; keyFoundColumn < Math.Max(actualTranslationsColumns, fileTranslationsColumns); ++keyFoundColumn)
                    {
                        try
                        {
                            newTranslations[currentActualTranslationRow, keyFoundColumn] = ReplaceSymbolWithNewLine(fileTranslations[keyFoundRow][keyFoundColumn]);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("ImportAndExportTranslations.Import", $"Key '{fileTranslations[keyFoundRow][0]}' > exception at column {keyFoundColumn}: {ex}");
                            return;
                        }
                    }

                    keyUpdated++;
                    if (keyFoundRow != 0)
                        Log.Debug("ImportAndExportTranslations.Import", $"Key '{fileTranslations[keyFoundRow][0]}' > successfully updated");
                }
                else
                {
                    Log.Debug("ImportAndExportTranslations.Import", $"Key '{currentDictionaryContent[currentActualTranslationRow, 0]}' > not found, skipped");
                    for (int column = 0; column < actualTranslationsColumns; ++column)
                        newTranslations[currentActualTranslationRow, column] = currentDictionaryContent[currentActualTranslationRow, column];

                    for (int column = actualTranslationsColumns; column < fileTranslationsColumns; ++column) // in case files has more columns than actual dictionary
                        newTranslations[currentActualTranslationRow, column] = "";
                }
            }

            localizationDictionary.Value = new UAValue(newTranslations);
            long newSize = GetDictionarySize(localizationDictionary);

            if (keyUpdated - 1 > 0)
                Log.Info("ImportAndExportTranslations.Import", $"Successfully updated {keyUpdated - 1} of {actualTranslationsRows - 1} keys into {localizationDictionary.BrowseName} dictionary");

            long difference = newSize - originalSize;
            if (difference > 0)
                Log.Info("ImportAndExportTranslations.Import", $"Added {difference} new keys into {localizationDictionary.BrowseName} dictionary");
            else if (difference < 0)
                Log.Info("ImportAndExportTranslations.Import", $"Removed {-difference} keys from {localizationDictionary.BrowseName} dictionary");
        }
        catch (Exception ex)
        {
            Log.Error("ImportAndExportTranslations.Import", $"Unable to import the translations: {ex}");
        }
    }

    /// <summary>
    /// Calculates the size of the localization dictionary by counting total cells minus the header row.
    /// </summary>
    /// <param name="dictionary">The localization dictionary variable to calculate the size for.</param>
    /// <returns>The size of the dictionary as an Int64 value representing the number of translation cells (excluding headers).</returns>
    private long GetDictionarySize(IUAVariable dictionary)
    {
        var dictionaryValue = dictionary.Value;
        if (dictionaryValue == null)
            return 0;

        string[,] dictionaryContent = (string[,])dictionary.Value.Value;
        int arraySize = dictionaryContent.GetLength(0) * dictionaryContent.GetLength(1);
        return arraySize - dictionaryContent.GetLength(0);
    }

    /// <summary>
    /// Retrieves the CSV file path from the CSVPath variable.
    /// Handles both project-relative paths (without '/') and absolute/resource paths (with '/').
    /// </summary>
    /// <returns>The URI string representing the CSV file path, or an empty string if the variable is not found or empty.</returns>
    private string GetCSVFilePath()
    {
        var csvPathVariable = LogicObject.GetVariable("CSVPath");
        if (csvPathVariable == null)
        {
            Log.Error("ImportAndExportTranslations", "CSVPath variable not found");
            return string.Empty;
        }

        string csvPath = csvPathVariable.Value;
        if (string.IsNullOrEmpty(csvPath))
        {
            Log.Error("ImportAndExportTranslations", "CSVPath variable is empty");
            return string.Empty;
        }

        return csvPath.Contains('/') 
            ? new ResourceUri(csvPath).Uri 
            : ResourceUri.FromProjectRelativePath(csvPath).Uri;
    }

    /// <summary>
    /// Retrieves the field delimiter character from the CharacterSeparator variable.
    /// </summary>
    /// <returns>The character separator, or null if the variable is not found or contains an invalid value.</returns>
    private char? GetCharacterSeparator()
    {
        var separatorVariable = LogicObject.GetVariable("CharacterSeparator");
        if (separatorVariable == null)
        {
            Log.Error("ImportAndExportTranslations", "CharacterSeparator variable not found");
            return null;
        }

        string separator = separatorVariable.Value;

        if (string.IsNullOrEmpty(separator) || separator.Length != 1)
        {
            Log.Error("ImportAndExportTranslations", "Wrong CharacterSeparator configuration. Please insert a char");
            return null;
        }

        return char.TryParse(separator, out char result) ? result : null;
    }

    /// <summary>
    /// Retrieves the WrapFields setting from the logic object.
    /// </summary>
    /// <returns>True if fields should be wrapped in quotes, false otherwise (or if the variable is not found).</returns>
    private bool GetWrapFields()
    {
        var wrapFieldsVariable = LogicObject.GetVariable("WrapFields");
        if (wrapFieldsVariable == null)
        {
            Log.Error("ImportAndExportTranslations", "WrapFields variable not found");
            return false;
        }

        return wrapFieldsVariable.Value;
    }

    /// <summary>
    /// Retrieves the localization dictionary specified by the LocalizationDictionary variable.
    /// If the variable is not set, attempts to retrieve the first available localization dictionary.
    /// </summary>
    /// <returns>The localization dictionary variable, or null if none is found.</returns>
    private IUAVariable GetDictionary()
    {
        var dictionaryVariable = LogicObject.GetVariable("LocalizationDictionary");
        if (dictionaryVariable == null)
        {
            Log.Info("ImportAndExportTranslations", "The first localization dictionary found will be used since the LocalizationDictionary variable cannot be not found");
            return GetDefaultDictionary();
        }

        NodeId nodeIdDictionaryValue = dictionaryVariable.Value;
        if (nodeIdDictionaryValue == null)
        {
            Log.Info("ImportAndExportTranslations", "The first localization dictionary found will be used since the LocalizationDictionary variable is not set");
            return GetDefaultDictionary();
        }

        var dictionaryNode = InformationModel.Get(nodeIdDictionaryValue);
        if (dictionaryNode == null)
        {
            Log.Error("ImportAndExportTranslations", "The node pointed by the LocalizationDictionary variable cannot be found");
            return null;
        }

        if (dictionaryNode is not IUAVariable resultDictionaryVariable || !resultDictionaryVariable.IsInstanceOf(FTOptix.Core.VariableTypes.LocalizationDictionary))
        {
            Log.Error("ImportAndExportTranslations", "The node pointed by the LocalizationDictionary variable is not a localization dictionary");
            return null;
        }

        return resultDictionaryVariable;
    }

    /// <summary>
    /// Retrieves the first localization dictionary found in the project that shares the same namespace index as the current project.
    /// </summary>
    /// <returns>The first localization dictionary in the project namespace, or null if none exists.</returns>
    private static IUAVariable GetDefaultDictionary()
    {
        var localizationDictionaryType = Project.Current.Context.GetNode(FTOptix.Core.VariableTypes.LocalizationDictionary);
        var localizationDictionaries = localizationDictionaryType.InverseRefs.GetNodes(OpcUa.ReferenceTypes.HasTypeDefinition);

        foreach (var dictionaryNode in localizationDictionaries)
        {
            if (dictionaryNode.NodeId.NamespaceIndex == Project.Current.NodeId.NamespaceIndex)
                return (IUAVariable)dictionaryNode;
        }

        return null;
    }

    #region CSVFileReader

    private class CsvFileReader : IDisposable
    {
        public char FieldDelimiter { get; set; } = ',';

        public char QuoteChar { get; set; } = '"';

        public bool WrapFields { get; set; } = false;

        public bool IgnoreMalformedLines { get; set; } = false;

        public CsvFileReader(string filePath)
        {
            streamReader = new StreamReader(filePath, System.Text.Encoding.UTF8);
        }

        public bool EndOfFile()
        {
            return streamReader.EndOfStream;
        }

        public List<string> ReadLine()
        {
            if (EndOfFile())
                return [];

            string line = streamReader.ReadLine();

            var result = WrapFields ? ParseLineWrappingFields(line) : ParseLineWithoutWrappingFields(line);

            currentLineNumber++;
            return result;
        }

        /// <summary>
        /// Reads all lines from the CSV file.
        /// </summary>
        /// <returns>A list where each element represents a row containing parsed field values.</returns>
        public List<List<string>> ReadAll()
        {
            var result = new List<List<string>>();
            while (!EndOfFile())
                result.Add(ReadLine());

            return result;
        }

        /// <summary>
        /// Parses a CSV line without field wrapping, splitting by the field delimiter.
        /// </summary>
        /// <param name="line">The line to parse.</param>
        /// <returns>A list containing the parsed field values.</returns>
        /// <exception cref="FormatException">Thrown when the line is empty and IgnoreMalformedLines is false.</exception>
        private List<string> ParseLineWithoutWrappingFields(string line)
        {
            if (string.IsNullOrEmpty(line) && !IgnoreMalformedLines)
                throw new FormatException($"Error processing line {currentLineNumber}. Line cannot be empty");

            if (line == null)
                return [];

            if (line.Length > 1 && IsQuoteChar(line, 0) && IsQuoteChar(line, line.Length - 1))
                line = line[1..^1];

            return line.Split(FieldDelimiter).ToList();
        }

        /// <summary>
        /// Parses a CSV line with quoted field wrapping, handling escaped quotes.
        /// </summary>
        /// <param name="line">The line to parse.</param>
        /// <returns>A list containing the parsed field values, or null if the line is malformed and IgnoreMalformedLines is true.</returns>
        /// <exception cref="FormatException">Thrown when the line is malformed and IgnoreMalformedLines is false.</exception>
        private List<string> ParseLineWrappingFields(string line)
        {
            var fields = new List<string>();
            var buffer = new StringBuilder("");
            bool fieldParsing = false;

            int i = 0;
            while (i < line.Length)
            {
                if (!fieldParsing)
                {
                    if (IsWhiteSpace(line, i))
                    {
                        ++i;
                        continue;
                    }

                    string lineErrorMessage = $"Error processing line {currentLineNumber}";
                    if (i == 0)
                    {
                        if (!IsQuoteChar(line, i))
                        {
                            if (IgnoreMalformedLines)
                                return [];
                            else
                                throw new FormatException($"{lineErrorMessage}. Expected quotation marks at column {i + 1}");
                        }

                        fieldParsing = true;
                    }
                    else
                    {
                        if (IsQuoteChar(line, i))
                        {
                            fieldParsing = true;
                        }
                        else if (!IsFieldDelimiter(line, i))
                        {
                            if (IgnoreMalformedLines)
                                return [];
                            else
                                throw new FormatException($"{lineErrorMessage}. Wrong field delimiter at column {i + 1}");
                        }
                    }

                    ++i;
                }
                else
                {
                    if (IsEscapedQuoteChar(line, i))
                    {
                        i += 2;
                        _ = buffer.Append(QuoteChar);
                    }
                    else if (IsQuoteChar(line, i))
                    {
                        fields.Add(buffer.ToString());
                        _ = buffer.Clear();
                        fieldParsing = false;
                        ++i;
                    }
                    else
                    {
                        _ = buffer.Append(line[i]);
                        ++i;
                    }
                }
            }

            return fields;
        }

        /// <summary>
        /// Checks if the character at the specified index is an escaped quote (two consecutive quote characters).
        /// </summary>
        /// <param name="line">The input string.</param>
        /// <param name="characterIndex">The index to check within the string.</param>
        /// <returns>True if the character at the index is a quote followed by another quote, false otherwise.</returns>
        private bool IsEscapedQuoteChar(string line, int characterIndex)
        {
            return line[characterIndex] == QuoteChar && characterIndex != line.Length - 1 && line[characterIndex + 1] == QuoteChar;
        }

        /// <summary>
        /// Checks if the character at the specified index matches the QuoteChar.
        /// </summary>
        /// <param name="line">The string to check.</param>
        /// <param name="characterIndex">The index of the character to check.</param>
        /// <returns>True if the character at the index is a quote character, false otherwise.</returns>
        private bool IsQuoteChar(string line, int characterIndex) => line[characterIndex] == QuoteChar;

        /// <summary>
        /// Checks if the character at the specified index matches the FieldDelimiter.
        /// </summary>
        /// <param name="line">The string to check.</param>
        /// <param name="characterIndex">The index of the character to check.</param>
        /// <returns>True if the character at the index is the field delimiter, false otherwise.</returns>
        private bool IsFieldDelimiter(string line, int characterIndex) => line[characterIndex] == FieldDelimiter;

        /// <summary>
        /// Checks if the character at the specified index is a whitespace character.
        /// </summary>
        /// <param name="line">The string to check.</param>
        /// <param name="characterIndex">The index of the character to examine.</param>
        /// <returns>True if the character at the index is whitespace, false otherwise.</returns>
        private static bool IsWhiteSpace(string line, int characterIndex) => char.IsWhiteSpace(line[characterIndex]);

        private readonly StreamReader streamReader;
        private int currentLineNumber = 1;

        #region IDisposable support

        private bool disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
                return;

            if (disposing)
            {
                streamReader?.Dispose();
            }

            disposed = true;
        }

        /// <summary>
        /// Releases all resources used by the CsvFileReader.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }

    #endregion

    #region CSVFileWriter

    private class CsvFileWriter : IDisposable
    {
        public char FieldDelimiter { get; set; } = ',';

        public char QuoteChar { get; set; } = '"';

        public bool WrapFields { get; set; } = false;

        public CsvFileWriter(string filePath) => streamWriter = new StreamWriter(filePath, false, System.Text.Encoding.UTF8);

        /// <summary>
        /// Writes a line to the CSV file with the specified field values.
        /// Fields are optionally wrapped in quotes and separated by the field delimiter.
        /// </summary>
        /// <param name="fields">An array of strings representing the field values to write.</param>
        public void WriteLine(string[] fields)
        {
            var stringBuilder = new StringBuilder();

            for (int i = 0; i < fields.Length; ++i)
            {
                if (WrapFields)
                    _ = stringBuilder.AppendFormat("{0}{1}{0}", QuoteChar, EscapeField(fields[i]));
                else
                    _ = stringBuilder.AppendFormat("{0}", fields[i]);

                if (i != fields.Length - 1)
                    _ = stringBuilder.Append(FieldDelimiter);
            }

            streamWriter.WriteLine(stringBuilder.ToString());
            streamWriter.Flush();
        }

        /// <summary>
        /// Escapes quote characters in a field by doubling them (e.g., " becomes "").
        /// </summary>
        /// <param name="field">The field to escape.</param>
        /// <returns>The field with quote characters escaped.</returns>
        private string EscapeField(string field)
        {
            string quoteCharString = QuoteChar.ToString();
            return field.Replace(quoteCharString, quoteCharString + quoteCharString);
        }

        private readonly StreamWriter streamWriter;

        #region IDisposable Support

        private bool disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
                return;

            if (disposing)
            {
                streamWriter?.Dispose();
            }

            disposed = true;
        }

        /// <summary>
        /// Releases all resources used by the CsvFileWriter.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }

    #endregion

    private static string ReplaceNewLineWithSymbol(string input) => input?.Replace("\n", NewLinePlaceHolder) ?? string.Empty;

    private static string ReplaceSymbolWithNewLine(string input) => input?.Replace(NewLinePlaceHolder, "\n") ?? string.Empty;

    private const string NewLinePlaceHolder = "\\n";

    private static readonly Regex localizationsRegex = new(@"^[a-z]{2}-[A-Z]{2}$", RegexOptions.Compiled);
}
