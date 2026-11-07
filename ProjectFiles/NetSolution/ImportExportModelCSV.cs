#region Using directives

using FTOptix.CommunicationDriver;
using FTOptix.Core;
using FTOptix.CoreBase;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using UAManagedCore;
using UAManagedCore.Logging;
using OpcUa = UAManagedCore.OpcUa;

#endregion

public class ImportExportModelCSV : BaseNetLogic
{
    /// <summary>
    /// This method builds a CSV file containing variable entries based on the specified logic object.
    /// <example>
    /// For example:
    /// <code>
    /// BuildVariablesCSVFile();
    /// </code>
    /// will write variable entries to the file located at "variablesList.csv".
    /// If the file path is empty, no action is taken.
    /// </example>
    /// </summary>
    /// <remarks>
    /// The method constructs the full file path using the provided logic object's value for the "VariablesNodeToBuildCSV" property.
    /// It then writes the variable entries to the CSV file specified by the constructed path.
    /// </remarks>
    /// <returns>
    /// Returns without returning an exception.
    /// </returns>
    [ExportMethod]
    public void BuildVariablesCSVFile()
    {
        string filePath = GenerateFullCSVFilePath("variablesList.csv", true);
        IUANode variablesNodeToBuildCSV = InformationModel.Get(LogicObject.GetVariable("VariablesNodeToBuildCSV").Value);
        if (filePath == string.Empty)
            return;

        LogNewEntry(LogLevel.Info, "Starting writing variables to CSV file...");
        WriteCSVFile(GenerateVariablesEntries(variablesNodeToBuildCSV), filePath);
    }

    /// <summary>
    /// This method exports the model variables' dynamic links to a CSV file.
    /// If no valid file path is provided, it exits without performing an operation.
    /// It logs a new entry indicating the start of the write process.
    /// The method uses the specified logic object's variable as the source for the model nodes to export.
    /// </summary>
    [ExportMethod]
    public void ExportModel()
    {
        IUANode modelsVariableFolder;
        string filePath = GenerateFullCSVFilePath(ModelDynamicLinkCSVFileName, true);

        modelsVariableFolder = InformationModel.Get(LogicObject.GetVariable("ModelNodeToExport").Value);
        if (filePath == string.Empty)
            return;

        LogNewEntry(LogLevel.Info, "Starting writing model variables dynamic links to CSV file...");
        WriteCSVFile(GenerateModelValueEntries(modelsVariableFolder), filePath);
    }

    /// <summary>
    /// Imports model data from a CSV file.
    /// If the file path is empty, the method exits without performing any action.
    /// Logs an informational message indicating that the process has started.
    /// Reads the contents of the specified CSV file and processes it.
    /// Creates or updates a model entry based on the parsed data.
    /// </summary>
    /// <remarks>
    /// The method assumes that 'ModelDynamicLinkCSVFileName' is a predefined constant representing the desired CSV filename for model dynamic link information.
    /// </remarks>
    [ExportMethod]
    public void ImportModel()
    {
        string filePath = GenerateFullCSVFilePath(ModelDynamicLinkCSVFileName, false);
        if (filePath == string.Empty)
            return;

        LogNewEntry(LogLevel.Info, "Starting reading model variables dynamic links from CSV file...");
        CreateOrUpdateModelEntry(ReadCSVFile(filePath));
    }

    /// <summary>
    /// Reads the contents of a CSV file and returns a list of string arrays representing the entries.
    /// Each entry corresponds to a line in the CSV file, split by the field delimiter.
    /// If the file path is empty or an error occurs during reading, it logs an error and returns null.
    /// </summary>
    /// <param name="filePath">The path to the CSV file to read.</param>
    /// <returns>A list of string arrays containing the CSV entries, or null if an error occurs.</returns>
    private List<string[]> ReadCSVFile(string filePath)
    {
        try
        {
            if (filePath == string.Empty)
                throw new NullReferenceException("File path is null or empty, cannot read CSV file");

            char fieldDelimiter = (char)GetFieldDelimiter();
            List<string[]> entries = new List<string[]>();
            LogNewEntry(LogLevel.Info, $"Working on CSV file {@"" + filePath + ""}");
            using (StreamReader reader = new StreamReader(filePath))
            {
                while (!reader.EndOfStream)
                {
                    entries.Add(reader.ReadLine().Split(fieldDelimiter));
                }
            }
            return entries;
        }
        catch (Exception ex)
        {
            LogNewEntry(LogLevel.Error, "Unable to read nodes from CSV file, error: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes a list of string arrays to a CSV file.
    /// Each string array represents a line in the CSV, with elements separated by the field delimiter.
    /// If the entries list is null or the file path is empty, it logs an error.
    /// </summary>
    /// <param name="entries">The list of string arrays to write to the CSV file.</param>
    /// <param name="filePath">The path to the CSV file to write to.</param>
    private void WriteCSVFile(List<string[]> entries, string filePath)
    {
        try
        {
            if (entries == null)
                throw new NullReferenceException("Entries list is null, cannot write CSV file");
            if (filePath == string.Empty)
                throw new NullReferenceException("File path is null or empty, cannot write CSV file");

            using (CSVFileWriter CSVFileWriter = new CSVFileWriter(filePath) { FieldDelimiter = GetFieldDelimiter().Value })
            {
                foreach (string[] entry in entries)
                {
                    CSVFileWriter.WriteLine(entry);
                }
            }
            LogNewEntry(LogLevel.Info, $"Successfully wrote {entries.Count} lines in {filePath}");
        }
        catch (Exception ex)
        {
            LogNewEntry(LogLevel.Error, "Unable to export node, error: " + ex.Message);
        }
    }

    /// <summary>
    /// This method generates a list of variable entries for a given UANode.
    /// If the starting node is null, it throws a NullReferenceException.
    /// It iterates through all child nodes of the starting node and adds their variable entries to the list.
    /// Upon encountering an exception, it logs the error message at the ERROR level.
    /// Returns the list of entries or null if an exception occurs.
    /// </summary>
    /// <param name="startingNode">The starting node from which to generate variable entries.</param>
    /// <returns>A list of string arrays containing the variable entries, or null if an exception occurs.</returns>
    private List<string[]> GenerateVariablesEntries(IUANode startingNode)
    {
        try
        {
            if (startingNode == null)
                throw new NullReferenceException("Starting node is null");

            List<string[]> entries = new List<string[]>
            {
                new string[3] { "Variable Name", "Variable Path" , "Variable Array size" }
            };
            foreach (IUANode childrenNode in startingNode.Children)
            {
                GetVariableNodeEntry(childrenNode, ref entries);
            }
            return entries;
        }
        catch (Exception ex)
        {
            LogNewEntry(LogLevel.Error, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// This method retrieves variable node entries from a given UANode.
    /// It checks if the node is a variable and not of type TagStructure.
    /// If so, it adds the node's browse name, path, and array dimension to the entries list.
    /// If the node has children, it recursively processes each child node.
    /// </summary>
    /// <param name="nodeToCheck">The node to check for variable entries.</param>
    /// <param name="entries">The list of entries to populate with variable information.</param>
    private void GetVariableNodeEntry(IUANode nodeToCheck, ref List<string[]> entries)
    {
        if (nodeToCheck.NodeClass == NodeClass.Variable && !nodeToCheck.GetType().IsAssignableTo(typeof(TagStructure)))
        {
            string arrayDimension = "";
            var nodeToCheckIUAVariable = nodeToCheck as IUAVariable;

            if (nodeToCheckIUAVariable.ArrayDimensions.Length > 0)
                arrayDimension = nodeToCheckIUAVariable.ArrayDimensions[0].ToString();

            entries.Add(new string[3] { nodeToCheck.BrowseName, MakeBrowsePath(nodeToCheck), arrayDimension });
        }
        else
        {
            if (nodeToCheck.Children.Count > 0)
            {
                foreach (IUANode childrenNode in nodeToCheck.Children)
                {
                    GetVariableNodeEntry(childrenNode, ref entries);
                }
            }
        }
    }

    /// <summary>
    /// This method generates model value entries for a given UANode.
    /// If the starting node is null, it throws a NullReferenceException.
    /// It iterates through all child nodes of the starting node and adds their model variable value entries to the list.
    /// Upon encountering an exception, it logs the error message at the ERROR level.
    /// Returns the list of entries or null if an exception occurs.
    /// </summary>
    /// <param name="startingNode">The starting node from which to generate model value entries.</param>
    /// <returns>A list of string arrays containing the model variable value entries, or null if an exception occurs.</returns>
    private List<string[]> GenerateModelValueEntries(IUANode startingNode)
    {
        try
        {
            if (startingNode == null)
                throw new NullReferenceException("Starting node is null");

            List<string[]> entries = new List<string[]>
            {
                CSVModelDynamicLinkHeader
            };
            foreach (IUANode childrenNode in startingNode.Children)
            {
                GetModelVariableValueEntry(childrenNode, entries);
            }
            return entries;
        }
        catch (UAManagedCore.CoreException ex)
        {
            if (ex.Subject == "")
                LogNewEntry(LogLevel.Error, ex.Message);
            else
                LogNewEntry(LogLevel.Error, $"DynamicLink on {ex.Subject}: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            LogNewEntry(LogLevel.Error, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// This method retrieves model variable value entries from a given UANode.
    /// It checks the node class and handles
    /// different cases for variables, objects, and object types.
    /// If the node is a variable, it retrieves its full path, data type, and array dimensions.
    /// It also handles dynamic links and aliases.
    /// If the node is an object or object type, it adds its path and type to the entries list.
    /// If the node has children, it recursively processes each child node.
    /// </summary>
    /// <param name="nodeToCheck">The node to check for model variable value entries.</param>
    /// <param name="entries">The list of entries to populate with model variable value information.</param>
    private void GetModelVariableValueEntry(IUANode nodeToCheck, List<string[]> entries)
    {
        var nodeType = nodeToCheck.GetType();
        if (nodeType.IsAssignableTo(typeOfVariableConditionEventDispatcher) ||
            nodeType.IsAssignableTo(typeOfVariableChangedEventDispatcher) ||
            nodeType.IsAssignableTo(typeOfVariableTransitionEventDispatcher) ||
            nodeType.IsAssignableTo(typeOfVariableRangeTransitionEventDispatcher))
        {
            Log.Warning($"Skipping variable {MakeBrowsePath(nodeToCheck)} of type {nodeToCheck.GetType().Name} as it is a Variable Event Dispatcher which are not supported by this script");
            return;
        }

        switch (nodeToCheck.NodeClass)
        {
            case NodeClass.Variable:
                string variableFullPath = MakeBrowsePath(nodeToCheck);
                string variableDataType = InformationModel.Get(((IUAVariable)nodeToCheck).DataType).BrowseName;
                var nodeToCheckIUAVariable = nodeToCheck as IUAVariable;
                uint variableArrayDimension = nodeToCheckIUAVariable.ArrayDimensions.Length > 0 ? nodeToCheckIUAVariable.ArrayDimensions[0] : 0;
                string sourceArrayIndex = string.Empty;
                string dynamicLinkMode = string.Empty;
                if (nodeToCheck.GetType().IsAssignableTo(typeof(Alias)))
                {
                    variableDataType = MakeBrowsePath(InformationModel.Get(((Alias)nodeToCheck).Kind));
                    NodeId aliasPointedNode = ((Alias)nodeToCheck).Value;
                    string aliasValue = aliasPointedNode != null ? MakeBrowsePath(InformationModel.Get(aliasPointedNode)) : string.Empty;
                    entries.Add(new string[7] { variableFullPath, MarkerAliasTypeIdentifier + variableDataType, "", "", aliasValue, "", "" });
                }
                else
                {
                    int j = nodeToCheckIUAVariable.ArrayDimensions.Length > 0 ? 0 : -1;
                    for (int i = j; i < variableArrayDimension; i++)
                    {
                        string dynamicLinkName = "DynamicLink" + (i >= 0 ? $"_{i}" : string.Empty);
                        string variableValue;
                        if (nodeToCheck.GetVariable(dynamicLinkName) is DynamicLink dynamicLink && !string.IsNullOrWhiteSpace(dynamicLink.Value))
                        {
                            PathResolverResult resolvePathResult = LogicObject.Context.ResolvePath(nodeToCheck, dynamicLink.Value);
                            if (resolvePathResult.AliasSpecification != null && resolvePathResult.AliasSpecification.AliasTokenPath != "")
                                variableValue = dynamicLink.Value;
                            else
                            {
                                variableValue = MakeBrowsePath(resolvePathResult.ResolvedNode);
                                if (Regex.IsMatch(dynamicLink.Value, "\\.\\d*?\\z"))
                                {
                                    string[] splitDynamicLinkValue = dynamicLink.Value.Value.ToString().Split(".");
                                    sourceArrayIndex += "." + splitDynamicLinkValue[^1];
                                }
                            }
                            foreach (uint index in resolvePathResult.ElementAccess.ArrayIndex)
                            {
                                sourceArrayIndex += $"{index}%";
                            }

                            if (sourceArrayIndex.Length > 1)
                                sourceArrayIndex = sourceArrayIndex.Remove(sourceArrayIndex.Length - 1);

                            dynamicLinkMode = ((int)dynamicLink.Mode).ToString();
                        }
                        else
                        {
                            if (variableArrayDimension > 0)
                            {
                                Array arrayValue = (Array)nodeToCheckIUAVariable.Value.Value;
                                variableValue = arrayValue.GetValue(i).ToString();
                            }
                            else
                            {
                                try
                                {
                                    variableValue = nodeToCheckIUAVariable?.Value?.Value.ToString();
                                }
                                catch (Exception ex)
                                {
                                    Log.Warning($"Unable to get value for variable {variableFullPath}, error: {ex.Message}");
                                    return;
                                }
                            }
                        }
                        string variableArrayIndex = variableArrayDimension > 0 ? i.ToString() : string.Empty;
                        entries.Add(new string[7] { variableFullPath, variableDataType, variableArrayDimension.ToString(), variableArrayIndex, variableValue, sourceArrayIndex, dynamicLinkMode });
                    }
                }
                return;

            case NodeClass.Object:
                entries.Add(new string[7] { MakeBrowsePath(nodeToCheck), MakeBrowsePath(((IUAObject)nodeToCheck).ObjectType), "", "", "", "", "" });
                break;

            case NodeClass.ObjectType:
                entries.Add(new string[7] { MakeBrowsePath(nodeToCheck), MarkerObjectTypeIdentifier + MakeBrowsePath(((IUAObjectType)nodeToCheck).SuperType), "", "", "", "", "" });
                break;
        }
        if (nodeToCheck.Children.Count > 0)
        {
            foreach (IUANode childrenNode in nodeToCheck.Children)
            {
                GetModelVariableValueEntry(childrenNode, entries);
            }
        }
    }

    /// <summary>
    /// Resets a dynamically linked variable by removing references and deleting the link.
    /// If the specified index is out of range or invalid, it calls the reset method for the entire variable instead.
    /// </summary>
    /// <param name="targetVariable">The variable containing the dynamically linked data.</param>
    /// <param name="arrayDimension">The dimension of the array.</param>
    /// <param name="arrayIndex">The index of the element within the array.</param>
    /// <remarks>
    /// If the specified index is valid and greater than zero, this method removes the reference to the dynamically linked item at the given index,
    /// then deletes the item itself.
    /// If the index is out of range, it resets the entire dynamically linked property.
    /// </remarks>
    private void ResetDynamicLink(IUAVariable targetVariable, uint arrayDimension, int arrayIndex)
    {
        if (arrayDimension > 0 && arrayIndex >= 0)
        {
            IUAVariable dynamicLinkToDelete = targetVariable.GetVariable($"DynamicLink_{arrayIndex}");
            if (dynamicLinkToDelete != null)
                targetVariable.Refs.RemoveReference(FTOptix.CoreBase.ReferenceTypes.HasDynamicLink, dynamicLinkToDelete.NodeId, false);

            try
            {
                dynamicLinkToDelete.Delete();
            }
            catch
            {
                // No DynamicLink to remove
            }
        }
        else
            targetVariable.ResetDynamicLink();
    }

    /// <summary>
    /// This method sets or updates an IUAVariable with a given value based on its current state.
    /// It handles arrays by iterating through them, updating each element, and then setting the variable's value accordingly.
    /// If the provided dimensions are valid (arrayDimension > 0 and arrayIndex >= 0), it processes the array; otherwise, it directly assigns the value to the variable.
    /// The method also increments a counter for tracking value operations performed.
    /// </summary>
    /// <param name="targetVariable">The IUAVariable whose value is to be updated.</param>
    /// <param name="arrayDimension">The dimension of the array to process.</param>
    /// <param name="arrayIndex">The index within the array to update.</param>
    /// <param name="value">The new value to assign to the array element or overall variable.</param>
    /// <remarks>
    /// For example:
    /// <code>
    /// // Example usage:
    /// var myVariable = new MyIAVariable();
    /// SetValueVariable(myVariable, 3, 2, "newValue");
    /// </code>
    /// would set myVariable at index 2 to "newValue".
    /// </remarks>
    private void SetValueVariable(IUAVariable targetVariable, uint arrayDimension, int arrayIndex, object value)
    {
        if (arrayDimension > 0 && arrayIndex >= 0)
        {
            Array arrayValue = (Array)targetVariable.Value.Value;
            arrayValue.SetValue(ManageObjectValue(arrayValue.GetValue(arrayIndex), value), arrayIndex);
            targetVariable.Value = new UAValue(arrayValue);
        }
        else
            targetVariable.Value = new UAValue(value);

        setOrUpdateValueCounter++;
    }

    /// <summary>
    /// This method sets a dynamic link for a given IUAVariable.
    /// It creates a new DynamicLink variable and assigns it the target variable's path.
    /// It also sets the mode and array index for the dynamic link.
    /// If the array index is specified, it sets the parent element access accordingly.
    /// Finally, it adds the dynamic link to the target variable's references.
    /// </summary>
    /// <param name="targetVariable">The IUAVariable to which the dynamic link will be set.</param>
    /// <param name="targetDynamicLink">The IUAVariable that will be the target of the dynamic link.</param>
    /// <param name="arrayVariableDimension">The dimension of the array variable.</param>
    /// <param name="arrayVariableIndex">The index of the array variable.</param>
    /// <param name="dynamicLinkArrayIndex">The index of the dynamic link array.</param>
    /// <param name="dynamicLinkMode">The mode of the dynamic link.</param>
    /// <param name="aliasDynamicLink">The alias for the dynamic link.</param>
    private void SetDynamicLink(IUAVariable targetVariable, IUAVariable targetDynamicLink, uint arrayVariableDimension, int arrayVariableIndex, string dynamicLinkArrayIndex, string dynamicLinkMode, string aliasDynamicLink)
    {
        int arrayIndex = -1;
        int mode = (int)DynamicLinkMode.ReadWrite;
        if (!string.IsNullOrWhiteSpace(dynamicLinkArrayIndex) &&
            !dynamicLinkArrayIndex.Contains("%") &&
            !dynamicLinkArrayIndex.StartsWith('.') &&
            !int.TryParse(dynamicLinkArrayIndex, out arrayIndex))
        {
            LogNewEntry(LogLevel.Warning, $"Unable to parse the array index {dynamicLinkArrayIndex}, dynamic link target {targetDynamicLink.BrowseName} for the variable {targetVariable.BrowseName} will be set without array specification");
        }

        if (!string.IsNullOrWhiteSpace(dynamicLinkMode) && !int.TryParse(dynamicLinkMode, out mode) && mode < (int)DynamicLinkMode.Read && mode > (int)DynamicLinkMode.ReadWrite)
        {
            LogNewEntry(LogLevel.Warning, $"Unable to parse the dynamic link mode {dynamicLinkMode}, dynamic link {targetDynamicLink.BrowseName} mode will be set to Read/Write");
            mode = (int)DynamicLinkMode.ReadWrite;
        }

        string dynamicLinkVariableBrowseName = "DynamicLink";
        if (arrayVariableDimension > 0 && arrayVariableIndex >= 0)
            dynamicLinkVariableBrowseName += $"_{arrayVariableIndex}";

        DynamicLink newDynamicLink = InformationModel.MakeVariable<DynamicLink>(dynamicLinkVariableBrowseName, FTOptix.Core.DataTypes.NodePath);

        newDynamicLink.Value = aliasDynamicLink.Length > 0 ? aliasDynamicLink : DynamicLinkPath.MakePath(targetVariable, targetDynamicLink);
        newDynamicLink.Mode = (DynamicLinkMode)mode;

        if (dynamicLinkArrayIndex.Contains("%"))
        {
            string[] splittedArrayMultiIndex = dynamicLinkArrayIndex.Split("%");
            string arrayMultiDimensionIndex = string.Empty;
            foreach (string singleArrayDimensionIndex in splittedArrayMultiIndex)
            {
                uint parsedIndex = 0;
                if (uint.TryParse(singleArrayDimensionIndex, out parsedIndex))
                    arrayMultiDimensionIndex += $"{parsedIndex},";
            }
            if (arrayMultiDimensionIndex.Length > 2)
                newDynamicLink.Value = $"{newDynamicLink.Value.Value}[{arrayMultiDimensionIndex.Remove(arrayMultiDimensionIndex.Length - 1)}]";
        }
        else if (dynamicLinkArrayIndex.StartsWith('.'))
        {
            newDynamicLink.Value = newDynamicLink.Value.Value + dynamicLinkArrayIndex;
        }
        else
            if (arrayIndex >= 0)
            newDynamicLink.Value = $"{newDynamicLink.Value.Value}[{arrayIndex}]";

        if (arrayVariableDimension > 0 && arrayVariableIndex >= 0)
            newDynamicLink.ParentElementAccess = new FTOptix.Core.ElementAccess(arrayVariableIndex);

        targetVariable.Refs.AddReference(FTOptix.CoreBase.ReferenceTypes.HasDynamicLink, newDynamicLink);
        newDynamicLink.SetModellingRuleRecursive();
        setOrUpdateDynamicLinkCounter++;
    }

    /// <summary>
    /// This method creates or updates a model entry based on the provided entries list.
    /// It iterates through the entries, validates their format, and processes each entry accordingly.
    /// </summary>
    /// <param name="entries">The list of entries to process.</param>
    private void CreateOrUpdateModelEntry(List<string[]> entries)
    {
        try
        {
            if (entries == null)
                throw new NullReferenceException("Entries list is null, cannot start the reading process");

            int entriesCount = -1;
            foreach (string[] entry in entries)
            {
                if (entry.Length != CSVModelDynamicLinkHeader.Length)
                    throw new InvalidDataException($"The elements count ({entry.Length}) of the entry is not equal to {CSVModelDynamicLinkHeader.Length} as it should be, the reading process is halted");

                entriesCount++;
                if (entriesCount == 0)
                {
                    continue;
                }
                uint variableArrayDimension = 0;
                int variableArrayDynamicLinkIndex = -1;
                string nodeBrowsePath = entry[0];
                string dataType = entry[1];
                string dynamicLinkPath = entry[4];
                string dynamicLinkArrayIndex = entry[5];
                string dynamicLinkMode = entry[6];
                uint.TryParse(entry[2], out variableArrayDimension);
                int.TryParse(entry[3], out variableArrayDynamicLinkIndex);
                NodeId dataTypeNodeId = GetVariableTypeNodeId(dataType);
                if (dataTypeNodeId != null)
                {
                    IUAVariable targetVariable = Project.Current.GetVariable(nodeBrowsePath);
                    if (targetVariable == null)
                    {
                        if (!MakeVariable(nodeBrowsePath, dataTypeNodeId, variableArrayDimension) || Project.Current.GetVariable(nodeBrowsePath) == null)
                        {
                            LogNewEntry(LogLevel.Error, $"Error in line {entriesCount} of CSV file: variable with path {nodeBrowsePath} does not exist.");
                            continue;
                        }
                        else
                            targetVariable = Project.Current.GetVariable(nodeBrowsePath);
                    }
                    if (string.IsNullOrWhiteSpace(dynamicLinkPath))
                    {
                        ResetDynamicLink(targetVariable, variableArrayDimension, variableArrayDynamicLinkIndex);
                        resetDynamicLinkCounter++;
                        continue;
                    }

                    IUANode targetDynamicLink = Project.Current.Get(dynamicLinkPath);
                    bool isAlias = dynamicLinkPath.Contains("{") && dynamicLinkPath.Contains("}");
                    bool isValue = targetDynamicLink == null && !isAlias;

                    if (!isValue && !isAlias && targetDynamicLink?.NodeClass != NodeClass.Variable)
                    {
                        LogNewEntry(LogLevel.Error, $"Error in line {entriesCount} of CSV file: dynamic link {dynamicLinkPath} is not a variable.");
                        continue;
                    }

                    ResetDynamicLink(targetVariable, variableArrayDimension, variableArrayDynamicLinkIndex);

                    if (isValue)
                    {
                        try
                        {
                            SetValueVariable(targetVariable, variableArrayDimension, variableArrayDynamicLinkIndex, dynamicLinkPath);
                        }
                        catch (Exception ex)
                        {
                            LogNewEntry(LogLevel.Warning, $"Unable to set value for {targetVariable}, Default value will be set ({ex.Message})");
                        }
                    }
                    else
                        SetDynamicLink(targetVariable, (IUAVariable)targetDynamicLink, variableArrayDimension, variableArrayDynamicLinkIndex, dynamicLinkArrayIndex, dynamicLinkMode, isAlias ? dynamicLinkPath : string.Empty);
                }
                else
                {
                    if (dataType.StartsWith(MarkerAliasTypeIdentifier))
                    {
                        NodeId aliasKind = GetDataTypeNodeId(dataType.Replace(MarkerAliasTypeIdentifier, string.Empty));
                        if (Project.Current.Get(nodeBrowsePath) is not Alias targetAlias)
                        {
                            if (aliasKind == null)
                            {
                                LogNewEntry(LogLevel.Error, $"Error in line {entriesCount} of CSV file: cannot find kind Data type with path {dataType} to create Alias with path {nodeBrowsePath}");
                                continue;
                            }
                            if (!MakeAlias(nodeBrowsePath, aliasKind))
                                LogNewEntry(LogLevel.Error, $"Error in line {entriesCount} of CSV file: kind for alias type with path {nodeBrowsePath} does not exist.");
                        }
                        else
                        {
                            if (aliasKind != null && targetAlias.Kind != aliasKind)
                                targetAlias.Kind = aliasKind;

                            IUANode targetDynamicLink = Project.Current.Get(dynamicLinkPath);

                            if (targetDynamicLink != null)
                                targetAlias.Owner.SetAlias(targetAlias.BrowseName, targetDynamicLink);
                        }
                    }
                    else if (dataType.StartsWith(MarkerObjectTypeIdentifier))
                    {
                        IUAObjectType targetObjectType = (IUAObjectType)Project.Current.Get(nodeBrowsePath);
                        if (targetObjectType == null)
                        {
                            NodeId objectSuperType = GetObjectTypeNodeId(dataType.Replace(MarkerObjectTypeIdentifier, string.Empty));
                            if (objectSuperType == null)
                            {
                                LogNewEntry(LogLevel.Error, $"Error in line {entriesCount} of CSV file: cannot find Object supertype with path {dataType} to create object type with path {nodeBrowsePath}");
                                continue;
                            }
                            if (!MakeObjectType(nodeBrowsePath, objectSuperType))
                                LogNewEntry(LogLevel.Error, $"Error in line {entriesCount} of CSV file: object type with path {nodeBrowsePath} does not exist.");
                        }
                    }
                    else
                    {
                        IUAObject targetObject = Project.Current.GetObject(nodeBrowsePath);
                        if (targetObject == null)
                        {
                            NodeId objectType = GetObjectTypeNodeId(dataType);
                            if (objectType == null)
                            {
                                LogNewEntry(LogLevel.Error, $"Error in line {entriesCount} of CSV file: cannot find Object type with path {dataType} to create object with path {nodeBrowsePath}");
                                continue;
                            }
                            if (!MakeObject(nodeBrowsePath, objectType))
                                LogNewEntry(LogLevel.Error, $"Error in line {entriesCount} of CSV file: object with path {nodeBrowsePath} does not exists.");
                        }
                    }
                }
            }
            LogNewEntry(LogLevel.Info, $"Successfully terminated the import of {entries.Count - 1} lines to merge dynamic link on models. {setOrUpdateDynamicLinkCounter} links set or updated, {resetDynamicLinkCounter} links reset, {setOrUpdateValueCounter} value set");
        }
        catch (Exception ex)
        {
            LogNewEntry(LogLevel.Error, ex.Message);
        }
    }

    /// <summary>
    /// This method retrieves the NodeId for a given data type path within the project.
    /// If the node type exists, it returns its NodeId; otherwise, it searches for the field information based on the provided data type path.
    /// The search considers both FTOptix.Core.VariableTypes and FTOptix.CommunicationDriver.VariableTypes.
    /// Returns the corresponding NodeId or null if no matching node type or field is found.
    /// </summary>
    /// <param name="dataTypePath">The data type path to retrieve the NodeId from.</param>
    /// <returns>The NodeId associated with the data type path, or null if none are found.</returns>
    private NodeId GetDataTypeNodeId(string dataTypePath)
    {
        IUANode nodeType = Project.Current.Get(dataTypePath);

        if (nodeType != null)
            return nodeType.NodeId;

        string[] splitTypePath = dataTypePath.Split('/');
        string nodeName = string.Empty;

        if (splitTypePath.Length <= 0)
            nodeName = dataTypePath;
        else
            nodeName = splitTypePath[splitTypePath.Length - 1];

        FieldInfo fieldType = typeof(FTOptix.Core.VariableTypes).GetField(nodeName);
        if (fieldType != null)
            return (NodeId)fieldType.GetValue(null);

        fieldType = typeof(FTOptix.CommunicationDriver.VariableTypes).GetField(nodeName);
        if (fieldType != null)
            return (NodeId)fieldType.GetValue(null);

        return null;
    }

    /// <summary>
    /// This method retrieves the NodeId for a variable type based on its path.
    /// If the data type ID can be directly obtained from the provided path, it returns that ID.
    /// Otherwise, it splits the path into segments and uses the last segment to look up the corresponding field within the FTOptix.Core.DataTypes class.
    /// Finally, it attempts to retrieve the node ID associated with the found field.
    /// Returns the retrieved NodeId or null if no valid node ID could be determined.
    /// </summary>
    /// <param name="dataTypePath">The path representing the variable's data type.</param>
    /// <returns>The NodeId corresponding to the variable type, or null if no valid node ID was found.</returns>
    private NodeId GetVariableTypeNodeId(string dataTypePath)
    {
        if (DataTypesHelper.GetDataTypeIdByName(dataTypePath) != null)
            return DataTypesHelper.GetDataTypeIdByName(dataTypePath);

        string[] splitTypePath = dataTypePath.Split('/');
        string nodeName = string.Empty;

        if (splitTypePath.Length <= 0)
            nodeName = dataTypePath;
        else
            nodeName = splitTypePath[splitTypePath.Length - 1];

        FieldInfo fieldType = typeof(FTOptix.Core.DataTypes).GetField(nodeName);

        if (fieldType != null)
            return (NodeId)fieldType.GetValue(null);
        else
            return null;
    }

    /// <summary>
    /// This method retrieves the NodeId for an object type by parsing its data type path.
    /// <example>
    /// For example:
    /// <code>
    /// NodeId nodeId = GetObjectTypeNodeId("DataType/ObjectName");
    /// </code>
    /// results in <c>nodeId</c>'s having the correct NodeId based on the provided data type path.
    /// </example>
    /// </summary>
    /// <param name="dataTypePath">The data type path containing the object type information.</param>
    /// <returns>
    /// The NodeId corresponding to the specified object type or null if no valid NodeId could be found.
    /// </returns>
    private NodeId GetObjectTypeNodeId(string dataTypePath)
    {
        IUANode nodeType = Project.Current.Get(dataTypePath);

        if (nodeType != null)
            return nodeType.NodeId;

        string[] splitTypePath = dataTypePath.Split('/');
        string nodeName = string.Empty;

        if (splitTypePath.Length <= 0)
            nodeName = dataTypePath;
        else
            nodeName = splitTypePath[splitTypePath.Length - 1];

        FieldInfo fieldType = typeof(OpcUa.ObjectTypes).GetField(nodeName);

        if (fieldType != null)
            return (NodeId)fieldType.GetValue(null);
        else
            return null;
    }

    /// <summary>
    /// This method retrieves the owner node from a given browse path.
    /// It splits the path into segments, constructs the full path excluding the last segment,
    /// and then looks up the owner node using the constructed path.
    /// If the owner cannot be found or an exception occurs during the process,
    /// it logs the error and sets the nodeName to an empty string, returning null.
    /// </summary>
    /// <param name="nodeBrowsePath">The browse path to extract the owner from.</param>
    /// <param name="nodeName">The name of the node associated with the owner.</param>
    /// <returns>The owner node as an IUANode object, or null if no valid owner could be found.</returns>
    private IUANode GetNodeOwnerFromPath(string nodeBrowsePath, out string nodeName)
    {
        try
        {
            string[] splitBrowsePath = nodeBrowsePath.Split('/');

            if (splitBrowsePath.Length <= 0)
                throw new NullReferenceException("Missing path");

            nodeName = splitBrowsePath[splitBrowsePath.Length - 1];
            string nodeOwner = string.Empty;

            for (int i = 0; i < splitBrowsePath.Length - 1; i++)
            {
                nodeOwner += splitBrowsePath[i] + (i == splitBrowsePath.Length - 2 ? "" : "/");
            }

            IUANode ownerNode = Project.Current.Get(nodeOwner);

            if (ownerNode == null)
                throw new NullReferenceException($"Cannot find owner {nodeOwner} for node {nodeName}. Full path: {nodeBrowsePath}");

            return ownerNode;
        }
        catch (Exception ex)
        {
            LogNewEntry(LogLevel.Error, ex.Message);
            nodeName = string.Empty;
            return null;
        }
    }

    /// <summary>
    /// This method creates an alias for a given browse path and node type.
    /// If successful, it returns true; otherwise, it logs an error message and returns false.
    /// </summary>
    /// <param name="aliasBrowsePath">The browse path for the alias.</param>
    /// <param name="kindType">The node type for the alias.</param>
    /// <returns>
    /// Returns true if the alias creation was successful, false otherwise.
    /// </returns>
    /// <remarks>
    /// Logs an error message with the exception's details when an error occurs during alias creation.
    /// </remarks>
    /// <example>
    /// For example:
    /// <code>
    /// bool success = MakeAlias("Alias/SomeObject", NodeId.UA_NodeClass.Variable);
    /// </code>
    /// Results in <c>success</c> being set to true or false based on the alias creation outcome.
    /// </example>
    private bool MakeAlias(string aliasBrowsePath, NodeId kindType)
    {
        try
        {
            string aliasName = string.Empty;
            IUANode ownerObjectType = GetNodeOwnerFromPath(aliasBrowsePath, out aliasName);
            Alias newAlias = InformationModel.MakeVariable<Alias>(aliasName, UAManagedCore.OpcUa.DataTypes.NodeId);
            newAlias.Kind = kindType;
            ownerObjectType.Add(newAlias);
            return true;
        }
        catch (Exception ex)
        {
            LogNewEntry(LogLevel.Error, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// This method creates a new object type based on the provided browse path and super-type.
    /// If successful, it adds the new object type as an owned entity under the specified owner object type.
    /// Otherwise, it logs the error and returns false.
    /// </summary>
    /// <param name="objectBrowsePath">The browse path of the object to be created.</param>
    /// <param name="objectSuperType">The super-type for the new object type.</param>
    /// <returns>
    /// True if the operation was successful, otherwise false.
    /// </returns>
    private bool MakeObjectType(string objectBrowsePath, NodeId objectSuperType)
    {
        try
        {
            string objectTypeName = string.Empty;
            IUANode ownerObjectType = GetNodeOwnerFromPath(objectBrowsePath, out objectTypeName);
            IUAObjectType newObjectType = InformationModel.MakeObjectType(objectTypeName, objectSuperType);
            ownerObjectType.Add(newObjectType);
            return true;
        }
        catch (Exception ex)
        {
            LogNewEntry(LogLevel.Error, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// This method attempts to make a new object with the specified browse path and type.
    /// If successful, it adds the newly created object to the owning node's collection.
    /// Otherwise, it logs an error message and returns false.
    /// </summary>
    /// <param name="objectBrowsePath">The path to the object to be made.</param>
    /// <param name="objectType">The type of the object to be made.</param>
    /// <returns>
    /// Returns true if the operation was successful, otherwise returns false.
    /// </returns>
    /// <remarks>
    /// Example usage:
    /// <code>
    /// bool success = MakeObject("path/to/object", NodeId.MyObject);
    /// </code>
    /// where "path/to/object" represents the browse path and MyObject is the desired NodeId for the object type.
    /// </remarks>
    private bool MakeObject(string objectBrowsePath, NodeId objectType)
    {
        try
        {
            string objectName = string.Empty;
            IUANode ownerObject = GetNodeOwnerFromPath(objectBrowsePath, out objectName);
            IUAObject newObject = InformationModel.MakeObject(objectName, objectType);
            ownerObject.Add(newObject);
            return true;
        }
        catch (Exception ex)
        {
            LogNewEntry(LogLevel.Error, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// This method creates a new variable based on the provided browse path, type, and optional dimension.
    /// If successful, it adds the newly created variable to the node's variables list.
    /// Otherwise, it logs an error message and returns false.
    /// </summary>
    /// <param name="variableBrowsePath">The path from which to retrieve the variable information.</param>
    /// <param name="variableType">The type of the variable to be created.</param>
    /// <param name="variableArrayDimension">Optional array dimension for the variable.</param>
    /// <returns>True if the variable was successfully created and added; otherwise, false.</returns>
    /// <remarks>
    /// Example usage:
    /// <code>
    /// bool success = MakeVariable("path/to/variable", NodeId.VariableType.Float, 3);
    /// </code>
    /// Returns true if the operation succeeds, false otherwise.
    /// The method attempts to get the owner node using the provided path, then constructs a new variable with the specified type and optional dimension.
    /// It logs an entry at the Error level if an exception is thrown during these operations.
    /// </remarks>
    private bool MakeVariable(string variableBrowsePath, NodeId variableType, uint variableArrayDimension)
    {
        try
        {
            string variableName = string.Empty;
            IUANode ownerVariable = GetNodeOwnerFromPath(variableBrowsePath, out variableName);
            uint[] arrayDimension = null;

            if (variableArrayDimension > 0)
                arrayDimension = new uint[] { variableArrayDimension };

            IUAVariable newVariable = InformationModel.MakeVariable(variableName, variableType, arrayDimension);
            ownerVariable.Add(newVariable);
            return true;
        }
        catch (Exception ex)
        {
            LogNewEntry(LogLevel.Error, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Logs an entry with the specified severity level and message.
    /// The log message includes the logic object's browse name and the calling member method.
    /// Severity levels are mapped to different logging methods as follows:
    /// - Info uses Log.Info
    /// - Warning uses Log.Warning
    /// - Error uses Log.Error
    /// - Debug uses Log.Debug
    /// - Verbose1 uses Log.Verbose1
    /// - Verbose2 uses Log.Verbose2
    /// </summary>
    /// <param name="entrySeverity">The severity level of the entry.</param>
    /// <param name="message">The message to be logged.</param>
    /// <param name="callerMethod">The name of the calling member method.</param>
    private void LogNewEntry(LogLevel entrySeverity, string message, [CallerMemberName] string callerMethod = "")
    {
        switch (entrySeverity)
        {
            case LogLevel.Info:
                Log.Info($"{LogicObject.BrowseName}.{callerMethod}", message);
                break;

            case LogLevel.Warning:
                Log.Warning($"{LogicObject.BrowseName}.{callerMethod}", message);
                break;

            case LogLevel.Error:
                Log.Error($"{LogicObject.BrowseName}.{callerMethod}", message);
                break;

            case LogLevel.Debug:
                Log.Debug($"{LogicObject.BrowseName}.{callerMethod}", message);
                break;

            case LogLevel.Verbose1:
                Log.Verbose1($"{LogicObject.BrowseName}.{callerMethod}", message);
                break;

            case LogLevel.Verbose2:
                Log.Verbose2($"{LogicObject.BrowseName}.{callerMethod}", message);
                break;
        }
    }

    /// <summary>
    /// This method generates the full file path for a CSV file based on the provided file name and writing mode.
    /// It creates the directory if it doesn't exist and handles file deletion if necessary.
    /// </summary>
    /// <param name="fileName">The name of the file to be created.</param>
    /// <param name="isWriting">Indicates whether the file is being written to.</param>
    /// <returns>The constructed CSV file path or an empty string if the path could not be created.</returns>
    private string GenerateFullCSVFilePath(string fileName, bool isWriting)
    {
        try
        {
            string folderPath = Path.GetDirectoryName(new ResourceUri(LogicObject.GetVariable(CSVFilePathVariableName)?.Value).Uri);

            if (string.IsNullOrEmpty(folderPath))
                throw new ArgumentException("CSV Folder path not set properly");

            Directory.CreateDirectory(folderPath);
            string fullFilePath = Path.Combine(folderPath, fileName);

            if (File.Exists(fullFilePath) && isWriting)
            {
                LogNewEntry(LogLevel.Warning, $"File {fullFilePath} already exists, will be deleted");
                File.Delete(fullFilePath);
            }
            return fullFilePath;
        }
        catch (Exception ex)
        {
            LogNewEntry(LogLevel.Error, ex.Message);
            return string.Empty;
        }
    }

    /// <summary>
    /// This method constructs a browse path for a given node by traversing up through its ancestors until reaching the specified ancestor.
    /// If no ancestor is provided, it defaults to the project's root node.
    /// The path is constructed as a hierarchical directory structure starting from the node's browse name and appending each parent's browse name with slashes.
    /// Example usage:
    /// <code>
    /// string path = MakeBrowsePath(new Node(), null);
    /// </code>
    /// Results in <c>path</c> containing the full browse path starting from the node's name and recursively adding parent names with slashes.
    /// </summary>
    /// <param name="node">The current node to build the path for.</param>
    /// <param name="ancestor">The ancestor node to stop at or null to use the project's root node.</param>
    /// <returns>A string representing the full browse path starting from the node's name and recursively including parent names with slashes.</returns>
    private string MakeBrowsePath(IUANode node, IUANode ancestor = null)
    {
        ancestor ??= Project.Current;

        if (node == null)
            return string.Empty;

        string path = node.BrowseName;
        var current = node.Owner;

        while (current != null && current != ancestor)
        {
            path = $"{current.BrowseName}/{path}";
            current = current.Owner;
        }
        return path;
    }

    /// <summary>
    /// Retrieves the field delimiter character for CSV files.
    /// </summary>
    private char? GetFieldDelimiter()
    {
        var separatorVariable = LogicObject.GetVariable(CharacterSeparatorVariableName);
        if (separatorVariable == null)
        {
            Log.Error(MethodBase.GetCurrentMethod().Name, "CharacterSeparator variable not found");
            return null;
        }

        string separator = separatorVariable.Value;

        if (separator.Length != 1 || separator == String.Empty)
        {
            Log.Error(MethodBase.GetCurrentMethod().Name, "Wrong CharacterSeparator configuration. Please insert a char");
            return null;
        }

        if (char.TryParse(separator, out char result))
            return result;

        return null;
    }

    /// <summary>
    /// This method converts the specified input value to the type of the target value using appropriate conversion functions.
    /// <example>
    /// For example:
    /// <code>
    /// object convertedValue = ManageObjectValue("currentString", "Hello");
    /// </code>
    /// results in <c>convertedValue</c> being "Hello" as a string.
    /// </example>
    /// </summary>
    /// <param name="targetValue">The target value whose type is used for conversion.</param>
    /// <param name="inputValue">The input value to be converted.</param>
    /// <returns>
    /// The converted value based on the type of the target value or the original value if no conversion is needed.
    /// </returns>
    private object ManageObjectValue(object targetValue, object inputValue)
    {
        switch (Type.GetTypeCode(targetValue.GetType()))
        {
            case TypeCode.Byte:
                return Convert.ToByte(inputValue);

            case TypeCode.SByte:
                return Convert.ToSByte(inputValue);

            case TypeCode.UInt16:
                return Convert.ToUInt16(inputValue);

            case TypeCode.UInt32:
                return Convert.ToUInt32(inputValue);

            case TypeCode.UInt64:
                return Convert.ToUInt64(inputValue);

            case TypeCode.Int16:
                return Convert.ToInt16(inputValue);

            case TypeCode.Int32:
                return Convert.ToInt32(inputValue);

            case TypeCode.Int64:
                return Convert.ToInt64(inputValue);

            case TypeCode.Decimal:
                return Convert.ToDecimal(inputValue);

            case TypeCode.Double:
                return Convert.ToDouble(inputValue);

            case TypeCode.Single:
                return Convert.ToSingle(inputValue);

            case TypeCode.String:
                return Convert.ToString(inputValue);

            default:
                return inputValue;
        }
    }

    private class CSVFileWriter : IDisposable
    {
        public char FieldDelimiter { get; set; } = ',';

        public char QuoteChar { get; set; } = '"';

        public bool WrapFields { get; set; } = false;
        public CSVFileWriter(string filePath)
        {
            CSVStreamWriter = new StreamWriter(filePath, false, System.Text.Encoding.UTF8);
        }

        /// <summary>
        /// Writes the provided array of fields to a CSV stream writer.
        /// </summary>
        /// <param name="fields">Array containing field names or values to be written.</param>
        /// <remarks>
        /// The method iterates over each element in the fields array, appending it to a StringBuilder along with appropriate formatting based on wrapFields status.
        /// If wrapFields is true, each field is quoted using quoteChar and escaped as needed before appending.
        /// Otherwise, the field is appended without modification.
        /// FieldDelimiter separates fields within the loop.
        /// After processing all elements, the resulting string from the StringBuilder is written to the CSVStreamWriter.
        /// The Flush() method ensures that any buffered data is immediately sent to the underlying stream.
        /// </remarks>
        public void WriteLine(string[] fields)
        {
            var stringBuilder = new StringBuilder();

            for (var i = 0; i < fields.Length; ++i)
            {
                if (WrapFields)
                    stringBuilder.AppendFormat("{0}{1}{0}", QuoteChar, EscapeField(fields[i]));
                else
                    stringBuilder.AppendFormat("{0}", fields[i]);

                if (i != fields.Length - 1)
                    stringBuilder.Append(FieldDelimiter);
            }

            CSVStreamWriter.WriteLine(stringBuilder.ToString());
            CSVStreamWriter.Flush();
        }

        /// <summary>
        /// This method escapes a given field by replacing the quotation character with its double representation.
        /// <example>
        /// For example:
        /// <code>
        /// string escapedField = EscapeField("Hello 'World'");
        /// </code>
        /// results in <c>escapedField</c>'s value being "Hello ''World''".
        /// </example>
        /// </summary>
        /// <param name="field">The field to escape.</param>
        /// <returns>
        /// A string containing the original field with quotation characters replaced as described.
        /// </returns>
        private string EscapeField(string field)
        {
            var quoteCharString = QuoteChar.ToString();
            return field.Replace(quoteCharString, quoteCharString + quoteCharString);
        }

        private readonly StreamWriter CSVStreamWriter;

        #region IDisposable Support

        private bool disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
                return;

            if (disposing)
                CSVStreamWriter.Dispose();

            disposed = true;
        }

        /// <summary>
        /// A helper method that calls Dispose(true) internally for proper cleanup.
        /// </summary>
        /// <param name="disposing">True if managed resources should be disposed.</param>
        public void Dispose()
        {
            Dispose(true);
        }

        #endregion
    }

    private const string CSVFilePathVariableName = "CSVFilePath";
    private const string CharacterSeparatorVariableName = "CharacterSeparator";
    private const string MarkerObjectTypeIdentifier = "%TYPE%";
    private const string MarkerAliasTypeIdentifier = "%ALIAS%";
    private readonly string[] CSVModelDynamicLinkHeader = { "Model path", "Type", "Variable Array Dimension", "Variable Array Index", "Dynamic link or Value", "Dynamic link array index", "Mode (0=R 1=W 2=RW)" };
    private const string ModelDynamicLinkCSVFileName = "modelList.csv";
    private int setOrUpdateDynamicLinkCounter = 0;
    private int setOrUpdateValueCounter = 0;
    private int resetDynamicLinkCounter = 0;

    private readonly System.Type typeOfVariableConditionEventDispatcher = typeof(VariableConditionEventDispatcher);
    private readonly System.Type typeOfVariableChangedEventDispatcher = typeof(VariableChangedEventDispatcher);
    private readonly System.Type typeOfVariableTransitionEventDispatcher = typeof(VariableTransitionEventDispatcher);
    private readonly System.Type typeOfVariableRangeTransitionEventDispatcher = typeof(VariableRangeTransitionEventDispatcher);

}
