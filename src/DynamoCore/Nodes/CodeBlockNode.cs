﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;
using GraphToDSCompiler;
using ProtoCore.AST.AssociativeAST;
using Dynamo.Core;
using Dynamo.Models;
using Dynamo.Utilities;
using ProtoCore.BuildData;
using ArrayNode = ProtoCore.AST.AssociativeAST.ArrayNode;
using Node = ProtoCore.AST.Node;
using Operator = ProtoCore.DSASM.Operator;

namespace Dynamo.Nodes
{
    [NodeName("Code Block")]
    [NodeCategory(BuiltinNodeCategories.CORE_INPUT)]
    [NodeDescription("Allows for code to be written")] //<--Change the descp :|
    public partial class CodeBlockNodeModel : NodeModel
    {
        private readonly List<Statement> codeStatements = new List<Statement>();
        private string code = "";
        private string codeToParse = "";
        private List<string> inputIdentifiers = new List<string>();
        private List<string> tempVariables = new List<string>();
        private string previewVariable;
        private AssociativeNode previewExpressionAST;
        private bool shouldFocus = true;

        #region Public Methods

        public CodeBlockNodeModel()
        {
            ArgumentLacing = LacingStrategy.Disabled;
        }

        public CodeBlockNodeModel(string userCode, Guid guid, WorkspaceModel workSpace)
        {
            ArgumentLacing = LacingStrategy.Disabled;
            this.code = userCode;
            this.GUID = guid;
            this.WorkSpace = workSpace;
            this.shouldFocus = false;
            DisableReporting();
            ProcessCode();
            RaisePropertyChanged("Code");
            RequiresRecalc = true;
            EnableReporting();
        }

        /// <summary>
        ///     The function sets the state of the node to an erraneous state and displays
        ///     the the string errorMessage as an error bubble on top of the node.
        ///     It also removes all the in ports and out ports as well. So that the user knows there is an error.
        /// </summary>
        /// <param name="errorMessage"> Error message to be displayed </param>
        public void DisplayError(string errorMessage)
        {
            DynamoLogger.Instance.Log("Error in Code Block Node");

            //Remove all ports
            int size = InPortData.Count;
            for (int i = 0; i < size; i++)
                InPortData.RemoveAt(0);
            size = OutPortData.Count;
            for (int i = 0; i < size; i++)
                OutPortData.RemoveAt(0);
            RegisterAllPorts();

            previewVariable = null;

            //Set the node state in error and display the message
            Error(errorMessage);
        }

        /// <summary>
        /// Formats user text by :
        /// 1.Removing whitespaces form the front and back (whitespaces -> space, tab or enter)
        /// 2.Removes unnecessary semi colons
        /// 3.Adds a semicolon at the end if needed
        /// </summary>
        /// <param name="inputCode"></param>
        /// <returns></returns>
        internal static string FormatUserText(string inputCode)
        {
            if (inputCode == null)
                return "";

            inputCode = inputCode.Replace("\r", "");
            inputCode = inputCode.Trim();

            string[] statements = inputCode.Split(';');
            inputCode = statements.Where(stmnt => stmnt.Any(c => !char.IsWhiteSpace(c)))
                                  .Aggregate("", (current, stmnt) => current + (stmnt + ";"));

            if (inputCode.Equals(""))
                return inputCode;

            //Add the ';' if required
            if (inputCode[inputCode.Length - 1] != ';')
                return inputCode.Insert(inputCode.Length, ";");
            return inputCode;
        }

        /// <summary>
        ///     Returns the names of all the variables defined in this code block.
        /// </summary>
        /// <returns>List containing all the names</returns>
        public List<string> GetDefinedVariableNames()
        {
            var defVarNames = new List<string>();
            foreach (Statement stmnt in codeStatements)
                defVarNames.AddRange(Statement.GetDefinedVariableNames(stmnt, true));
            return defVarNames;
        }

        #endregion

        #region Properties

        public override bool IsConvertible
        {
            get
            {
                return true;
            }
        }

        public string Code
        {
            get { return code; }

            set
            {
                if (code == null || !code.Equals(value))
                {
                    if (value != null)
                    {
                        DisableReporting();
                        {
                            WorkSpace.UndoRecorder.BeginActionGroup();

                            var inportConnections = new OrderedDictionary();
                            var outportConnections = new OrderedDictionary();
                            //Save the connectors so that we can recreate them at the correct positions
                            SaveAndDeleteConnectors(inportConnections, outportConnections);

                            if (code == "")
                            {
                                WorkSpace.UndoRecorder.PopFromUndoGroup();
                                WorkSpace.UndoRecorder.RecordCreationForUndo(this);
                            }
                            else
                                WorkSpace.UndoRecorder.RecordModificationForUndo(this);
                            code = value;
                            ProcessCode();

                            //Recreate connectors that can be reused
                            LoadAndCreateConnectors(inportConnections, outportConnections);
                            WorkSpace.UndoRecorder.EndActionGroup();
                        }
                        RaisePropertyChanged("Code");
                        RequiresRecalc = true;
                        EnableReporting();
                        ReportPosition();
                        if (WorkSpace != null)
                            WorkSpace.Modified();
                    }
                    else
                        code = null;
                }
            }
        }

        public string CodeToParse
        {
            get { return codeToParse; }
        }

        /// <summary>
        /// Temporary variables that generated in code.
        /// </summary>
        public List<string> TempVariables
        {
            get { return tempVariables; }
        }

        #endregion

        #region Protected Methods

        public override string VariableToPreview
        {
            get { return (State == ElementState.Error) ? null : previewVariable; }
        }

        protected override void SaveNode(XmlDocument xmlDoc, XmlElement nodeElement, SaveContext context)
        {
            base.SaveNode(xmlDoc, nodeElement, context);
            var helper = new XmlElementHelper(nodeElement);
            helper.SetAttribute("CodeText", code);
            helper.SetAttribute("ShouldFocus", shouldFocus);
        }

        protected override void LoadNode(XmlNode nodeElement)
        {
            base.LoadNode(nodeElement);
            var helper = new XmlElementHelper(nodeElement as XmlElement);
            code = helper.ReadString("CodeText");
            ProcessCodeDirect();
            shouldFocus = helper.ReadBoolean("ShouldFocus");
        }

        protected override bool UpdateValueCore(string name, string value)
        {
            if (name == "Code")
            {
                Code = value;
                return true;
            }

            return base.UpdateValueCore(name, value);
        }

        protected override void SerializeCore(XmlElement element, SaveContext context)
        {
            base.SerializeCore(element, context);
            var helper = new XmlElementHelper(element);
            helper.SetAttribute("CodeText", code);
            helper.SetAttribute("ShouldFocus", shouldFocus);
        }

        protected override void DeserializeCore(XmlElement element, SaveContext context)
        {
            base.DeserializeCore(element, context);
            if (context == SaveContext.Undo)
            {
                var helper = new XmlElementHelper(element);
                shouldFocus = helper.ReadBoolean("ShouldFocus");
                code = helper.ReadString("CodeText");
                ProcessCodeDirect();
            }
        }

        internal override IEnumerable<AssociativeNode> BuildAst(List<AssociativeNode> inputAstNodes)
        {
            //var unboundIdentifiers = new List<string>();
            var resultNodes = new List<AssociativeNode>();
            CodeBlockNode commentNode;
            CodeBlockNode codeBlock = null;
            string finalCode = "";

            // Define unbound variables if necessary
            if (inputIdentifiers != null && inputIdentifiers.Count > 0)
            {
                if (null == inputAstNodes || inputAstNodes.Count != inputIdentifiers.Count)
                {
                    // This is already an invalid state. Return an empty resultNodes
                    // A more robust fix is perhaps the ASTBuilder to ignore this node altogether if an error is reported
                    return resultNodes;
                 }

                var initStatements = new StringBuilder();
                for (int i = 0; i < inputIdentifiers.Count; ++i)
                {
                    AssociativeNode astNode = inputAstNodes[i];
                    if (astNode is IdentifierNode)
                    {
                        string unboundVar = inputIdentifiers[i];
                        string inputVar = GraphUtilities.ASTListToCode(new List<AssociativeNode> { astNode });
                        if (!string.Equals(unboundVar, inputVar))
                        {
                            initStatements.Append(unboundVar);
                            initStatements.Append(" = ");
                            initStatements.Append(inputVar);
                            initStatements.Append(";");
                        }
                    }
                }
                finalCode = initStatements.ToString();
            }

            try
            {
                codeBlock = GraphUtilities.Parse(finalCode, out commentNode) as CodeBlockNode;
            }
            catch (Exception ex)
            {
                State = ElementState.Error;
                DynamoLogger.Instance.Log("Failed to build AST for code block node. Error: " + ex.Message);
            }

            if (codeBlock != null)
                resultNodes.AddRange(codeBlock.Body);

            foreach (var stmnt in codeStatements)
            {
                var astNode = ProtoCore.Utils.NodeUtils.Clone(stmnt.AstNode);
                resultNodes.Add(astNode as ProtoCore.AST.AssociativeAST.AssociativeNode);
            }

            resultNodes.Add(ProtoCore.Utils.NodeUtils.Clone(previewExpressionAST));

            return resultNodes;
        }

        public override IdentifierNode GetAstIdentifierForOutputIndex(int portIndex)
        {
            if (State == ElementState.Error)
                return null;

            int statementIndex = -1;
            while (portIndex >= 0)
            {
                statementIndex++;
                if (RequiresOutPort(codeStatements[statementIndex], statementIndex))
                    portIndex--;
            }

            return (codeStatements[statementIndex].AstNode as BinaryExpressionNode).LeftNode as IdentifierNode;
        }

        #endregion

        #region Private Methods

        private void ProcessCodeDirect()
        {
            ProcessCode();
            RaisePropertyChanged("Code");
            RequiresRecalc = true;
            if (WorkSpace != null)
                WorkSpace.Modified();
        }

        private void ProcessCode()
        {
            //Format user test
            code = FormatUserText(code);

            //New code => Revamp everything
            codeStatements.Clear();

            if (Code.Equals("")) //If its null then remove all the ports
            {
                SetPorts(new List<string>());
                return;
            }

            //Parse the text and assign each AST node to a statement instance
            codeToParse = code;
            List<string> unboundIdentifiers = new List<string>();
            List<ProtoCore.AST.Node> parsedNodes;
            List<ProtoCore.BuildData.ErrorEntry> errors;
            List<ProtoCore.BuildData.WarningEntry> warnings;

            try
            {
                if (GraphToDSCompiler.GraphUtilities.Parse(ref codeToParse, out parsedNodes, out errors,
                    out  warnings, unboundIdentifiers, out tempVariables) && parsedNodes != null)
                {
                    //Create an instance of statement for each code statement written by the user
                    for (int i = 0; i < parsedNodes.Count; i++)
                    {
                        var parsedNode = parsedNodes[i];
                        Statement tempStatement;

                        //Create and save a statement variable from the astnodes generated
                        tempStatement = Statement.CreateInstance(parsedNode);
                        codeStatements.Add(tempStatement);
                    }

                    SetPreviewVariable(parsedNodes[parsedNodes.Count-1] as BinaryExpressionNode);
                }
                else
                {
                    if (errors == null)
                        DisplayError("Errors not getting sent from compiler to UI");

                    //Found errors. Get the error message strings and use it to call the DisplayError function
                    if (errors != null)
                    {
                        string errorMessage = "";
                        int i = 0;
                        for (; i < errors.Count - 1; i++)
                            errorMessage += (errors[i].Message + "\n");
                        errorMessage += errors[i].Message;
                        DisplayError(errorMessage);
                    }
                    return;
                }
            }
            catch (Exception e)
            {
                DisplayError(e.Message);
                previewVariable = null;
                return;
            }

            //Make sure variables have not been declared in other Code block nodes.
            string redefinedVariable = this.WorkSpace.GetFirstRedefinedVariable(this);
            if (redefinedVariable != null)
            {
                DisplayError(redefinedVariable + " is already defined");
                return;
            }

            SetPorts(unboundIdentifiers); //Set the input and output ports based on the statements
        }

        private void SetPreviewVariable(BinaryExpressionNode lastStatement)
        {
            previewVariable = "temp" + Guid.NewGuid().ToString();
            previewVariable = previewVariable.Replace('-', '_');
            CodeBlockNode commentNode;
            string finalCode = previewVariable + "=1;";

            try
            {
                previewExpressionAST = (GraphUtilities.Parse(finalCode, out commentNode) as CodeBlockNode).Body[0];
            }
            catch (Exception ex)
            {
                State = ElementState.Error;
                DynamoLogger.Instance.Log("Failed to build AST for code block node. Error: " + ex.Message);
            }

            if (lastStatement == null)
                throw new ArgumentNullException("Statement not a binary expression node");

            (previewExpressionAST as BinaryExpressionNode).RightNode = lastStatement.LeftNode;
        }

        /// <summary>
        ///     Creates the inport and outport data based on the statements generated form the user code
        /// </summary>
        /// <param name="unboundIdentifiers"> List of unbound identifiers to be used an inputs</param>
        private void SetPorts(List<string> unboundIdentifiers)
        {
            inputIdentifiers = unboundIdentifiers;

            InPortData.Clear();
            OutPortData.Clear();
            if (codeStatements.Count == 0 || codeStatements == null)
            {
                RegisterAllPorts();
                return;
            }

            SetInputPorts(unboundIdentifiers);

            //Since output ports need to be aligned with the statements, calculate the margins
            //needed based on the statement lines and add them to port data.
            List<double> verticalMargin = CalculateMarginInPixels();
            SetOutputPorts(verticalMargin);

            RegisterAllPorts();
        }

        /// <summary>
        ///     Creates the output ports with the necessary margins for port alignment
        /// </summary>
        /// <param name="verticalMargin"> Distance between the consequtive output ports </param>
        private void SetOutputPorts(List<double> verticalMargin)
        {
            int outportCount = 0;
            for (int i = 0; i < codeStatements.Count; i++)
            {
                Statement s = codeStatements[i];
                if (RequiresOutPort(s, i))
                {
                    string nickName = Statement.GetDefinedVariableNames(s, true)[0];

                    if (nickName.StartsWith("temp") && nickName.Length > 9) // Do a better check
                        nickName = "Statement Output"; //Set tool tip incase of random var name

                    OutPortData.Add(
                        new PortData(">", nickName, typeof(object)) { VerticalMargin = verticalMargin[outportCount] });
                    outportCount++;
                }
            }
        }

        /// <summary>
        ///     Set a port for each different unbound identifier
        /// </summary>
        private void SetInputPorts(IEnumerable<string> unboundIdentifier)
        {
            foreach (string name in unboundIdentifier)
            {
                string portName = name;
                if (portName.Length > Configurations.MaxPortNameLength)
                    portName = portName.Remove(Configurations.MaxPortNameLength - 3) + "...";
                InPortData.Add(new PortData(portName, name, typeof(object)));
            }
        }

        /// <summary>
        ///     Based on the start line of ech statement and type, it returns a list of
        ///     top margins required for the ports
        /// </summary>
        private List<double> CalculateMarginInPixels()
        {
            var result = new List<double>();
            int currentOffset = 1; //Used to mark the line immediately after the last output port line
            double initialMarginRequired = 4;
            for (int i = 0; i < codeStatements.Count; i++)
            {
                //Dont calculate margin for ports that dont require a port
                if (!RequiresOutPort(codeStatements[i], i))
                    continue;

                //Margin = diff between this line and prev port line x port height
                double margin;
                if (codeStatements[i].StartLine - currentOffset >= 0)
                {
                    margin = (codeStatements[i].StartLine - currentOffset) * 20;
                    currentOffset = codeStatements[i].StartLine + 1;
                }
                else
                {
                    margin = 0.0;
                    currentOffset += 1;
                }
                result.Add(margin + initialMarginRequired);
                initialMarginRequired = 0;
            }
            return result;
        }

        /// <summary>
        ///     Checks wheter an outport is required for a given statement. An outport is not required
        ///     if there are no defined variables or if any of the defined variables have been
        ///     declared again later on in the code block
        /// </summary>
        /// <param name="s"> Statement to check the port</param>
        /// <param name="pos"> Position of the statement in codeStatements</param>
        /// <returns></returns>
        private bool RequiresOutPort(Statement s, int pos)
        {
            List<string> defVariables = Statement.GetDefinedVariableNames(s, true);

            //Check if defined variables exist
            if (defVariables.Count == 0)
                return false;

            //Check if variable has been redclared later on in the CBN
            foreach (string varName in defVariables)
            {
                for (int i = pos + 1; i < codeStatements.Count; i++)
                {
                    List<string> laterDefVariables = Statement.GetDefinedVariableNames(codeStatements[i], true);
                    if (laterDefVariables.Contains(varName))
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        ///     Deletes all the connections and saves their data (the start and end port)
        ///     so that they can be recreated if needed.
        /// </summary>
        /// <param name="portConnections">A list of connections that will be destroyed</param>
        private void SaveAndDeleteConnectors(OrderedDictionary inportConnections, OrderedDictionary outportConnections)
        {
            //----------------------------Inputs---------------------------------
            for (int i = 0; i < InPorts.Count; i++)
            {
                PortModel portModel = InPorts[i];
                string portName = portModel.ToolTipContent;
                if (portModel.Connectors.Count != 0)
                {
                    inportConnections.Add(portName, new List<PortModel>());
                    foreach (ConnectorModel connector in portModel.Connectors)
                    {
                        (inportConnections[portName] as List<PortModel>).Add(connector.Start);
                        WorkSpace.UndoRecorder.RecordDeletionForUndo(connector);
                    }
                }
                else
                    inportConnections.Add(portName, null);
            }

            //Delete the connectors
            foreach (PortModel inport in InPorts)
                inport.DestroyConnectors();

            //Clear out all the port models
            for (int i = InPorts.Count - 1; i >= 0; i--)
                InPorts.RemoveAt(i);


            //----------------------------Outputs---------------------------------
            for (int i = 0; i < OutPorts.Count; i++)
            {
                PortModel portModel = OutPorts[i];
                string portName = portModel.ToolTipContent;
                if (portModel.ToolTipContent.Equals("Statement Output"))
                    portName += i.ToString(CultureInfo.InvariantCulture);
                if (portModel.Connectors.Count != 0)
                {
                    outportConnections.Add(portName, new List<PortModel>());
                    foreach (ConnectorModel connector in portModel.Connectors)
                    {
                        (outportConnections[portName] as List<PortModel>).Add(connector.End);
                        WorkSpace.UndoRecorder.RecordDeletionForUndo(connector);
                    }
                }
                else
                    outportConnections.Add(portName, null);
            }

            //Delete the connectors
            foreach (PortModel outport in OutPorts)
                outport.DestroyConnectors();

            //Clear out all the port models
            for (int i = OutPorts.Count - 1; i >= 0; i--)
                OutPorts.RemoveAt(i);
        }

        /// <summary>
        ///     Now that the portData has been set for the new ports, we recreate the connections we
        ///     so mercilessly destroyed, restoring peace and balance to the world once again.
        /// </summary>
        /// <param name="outportConnections"> List of the connections that were killed</param>
        private void LoadAndCreateConnectors(OrderedDictionary inportConnections, OrderedDictionary outportConnections)
        {
            //----------------------------Inputs---------------------------------
            /* Input Port connections are matched only if the name is the same */
            for (int i = 0; i < InPortData.Count; i++)
            {
                string varName = InPortData[i].ToolTipString;
                if (inportConnections.Contains(varName))
                {
                    if (inportConnections[varName] != null)
                    {
                        foreach (var startPortModel in (inportConnections[varName] as List<PortModel>))
                        {
                            PortType p;
                            NodeModel startNode = startPortModel.Owner;
                            ConnectorModel connector = ConnectorModel.Make(startNode, this,
                                startNode.GetPortIndexAndType(startPortModel, out p), i, PortType.INPUT);
                            this.WorkSpace.Connectors.Add(connector);
                            this.WorkSpace.UndoRecorder.RecordCreationForUndo(connector);
                        }
                        outportConnections[varName] = null;
                    }
                }
            }

            //----------------------------Outputs--------------------------------
            /*The matching is done in three parts:
             *Step 1:
             *   First, it tries to match the connectors wrt to the defined 
             *   variable name. Hence it first checks to see if any of the old 
             *   variable names are present. If so, if there were any connectors 
             *   presnt then it makes the new connectors. As it iterates through 
             *   the new ports, it also finds the ports that didnt exist before
             */
            List<int> undefinedIndices = new List<int>();
            for (int i = 0; i < OutPortData.Count; i++)
            {
                string varName = OutPortData[i].ToolTipString;
                if (outportConnections.Contains(varName))
                {
                    if (outportConnections[varName] != null)
                    {
                        foreach (var endPortModel in (outportConnections[varName] as List<PortModel>))
                        {
                            PortType p;
                            NodeModel endNode = endPortModel.Owner;
                            ConnectorModel connector = ConnectorModel.Make(this, endNode, i,
                                endNode.GetPortIndexAndType(endPortModel, out p), PortType.INPUT);
                            this.WorkSpace.Connectors.Add(connector);
                            this.WorkSpace.UndoRecorder.RecordCreationForUndo(connector);
                        }
                        outportConnections[varName] = null;
                    }
                }
                else
                    undefinedIndices.Add(i);
            }

            /*
             *Step 2:
             *   The second priority is to match the connections to the previous 
             *   indices. For all the ports that were not previously defined, it 
             *   now checks if that "numbered" port had any connections 
             *   previously, ie, if the old third port had 2 connections, then 
             *   these would go to the new 3rd port (if it is not a variable that
             *   was defined before)
             */
            for (int i = 0; i < undefinedIndices.Count; i++)
            {
                int index = undefinedIndices[i];
                if (index < outportConnections.Count && outportConnections[index] != null)
                {
                    foreach (PortModel endPortModel in (outportConnections[index] as List<PortModel>))
                    {
                        PortType p;
                        NodeModel endNode = endPortModel.Owner;
                        ConnectorModel connector = ConnectorModel.Make(this, endNode, index,
                            endNode.GetPortIndexAndType(endPortModel, out p), PortType.INPUT);
                        WorkSpace.Connectors.Add(connector);
                        WorkSpace.UndoRecorder.RecordCreationForUndo(connector);
                    }
                    outportConnections[index] = null;
                    undefinedIndices.Remove(index);
                    i--;
                }
            }

            /*
             *Step 2:
             *   The final step. Now that the priorties are finished, the 
             *   function tries to reuse any existing connections by attaching 
             *   them to any ports that have not already been given connections
             */
            List<List<PortModel>> unusedConnections = new List<List<PortModel>>();
            foreach (List<PortModel> portModelList in outportConnections.Values.Cast<List<PortModel>>())
            {
                if (portModelList == null)
                    continue;
                unusedConnections.Add(portModelList);
            }
            while (undefinedIndices.Count > 0 && unusedConnections.Count != 0)
            {
                foreach (PortModel endPortModel in unusedConnections[0])
                {
                    PortType p;
                    NodeModel endNode = endPortModel.Owner;
                    ConnectorModel connector = ConnectorModel.Make(
                        this,
                        endNode,
                        undefinedIndices[0],
                        endNode.GetPortIndexAndType(endPortModel, out p),
                        PortType.INPUT);
                    WorkSpace.Connectors.Add(connector);
                    WorkSpace.UndoRecorder.RecordCreationForUndo(connector);
                }
                undefinedIndices.RemoveAt(0);
                unusedConnections.RemoveAt(0);
            }
        }

        #endregion
    }


    public class Statement
    {
        #region Enums

        #region State enum

        public enum State
        {
            Normal,
            Warning,
            Error
        }

        #endregion

        #region StatementType enum

        public enum StatementType
        {
            None,
            Expression,
            Literal,
            Collection,
            AssignmentVar,
            FuncDeclaration
        }

        #endregion

        #endregion

        private readonly List<Variable> definedVariables = new List<Variable>();
        private readonly List<Variable> referencedVariables;
        private readonly List<Statement> subStatements = new List<Statement>();

        #region Public Methods
        public static Statement CreateInstance(Node parsedNode)
        {
            if (parsedNode == null)
                throw new ArgumentNullException();

            return new Statement(parsedNode);
        }

        public static void GetReferencedVariables(Node astNode, List<Variable> refVariableList)
        {
            //DFS Search to find all identifier nodes
            if (astNode == null)
                return;
            if (astNode is FunctionCallNode)
            {
                var currentNode = astNode as FunctionCallNode;
                foreach (AssociativeNode node in currentNode.FormalArguments)
                    GetReferencedVariables(node, refVariableList);
            }
            else if (astNode is IdentifierNode)
            {
                var resultVariable = new Variable(astNode as IdentifierNode);
                refVariableList.Add(resultVariable);
                GetReferencedVariables((astNode as IdentifierNode).ArrayDimensions, refVariableList);
            }
            else if (astNode is ArrayNode)
            {
                var currentNode = astNode as ArrayNode;
                GetReferencedVariables(currentNode.Expr, refVariableList);
                GetReferencedVariables(currentNode.Type, refVariableList);
            }
            else if (astNode is ExprListNode)
            {
                var currentNode = astNode as ExprListNode;
                foreach (AssociativeNode node in currentNode.list)
                    GetReferencedVariables(node, refVariableList);
            }
            else if (astNode is FunctionDotCallNode)
            {
                var currentNode = astNode as FunctionDotCallNode;
                GetReferencedVariables(currentNode.FunctionCall, refVariableList);
            }
            else if (astNode is InlineConditionalNode)
            {
                var currentNode = astNode as InlineConditionalNode;
                GetReferencedVariables(currentNode.ConditionExpression, refVariableList);
                GetReferencedVariables(currentNode.TrueExpression, refVariableList);
                GetReferencedVariables(currentNode.FalseExpression, refVariableList);
            }
            else if (astNode is RangeExprNode)
            {
                var currentNode = astNode as RangeExprNode;
                GetReferencedVariables(currentNode.FromNode, refVariableList);
                GetReferencedVariables(currentNode.ToNode, refVariableList);
                GetReferencedVariables(currentNode.StepNode, refVariableList);
            }
            else if (astNode is BinaryExpressionNode)
            {
                var currentNode = astNode as BinaryExpressionNode;
                GetReferencedVariables(currentNode.RightNode, refVariableList);
            }
            else
            {
                //Its could be something like a literal
                //Or node not completely implemented YET
            }
        }

        /// <summary>
        ///     Returns the names of the variables that have been referenced in the statement
        /// </summary>
        /// <param name="s"> Statement whose variable names to be got.</param>
        /// <param name="onlyTopLevel"> Bool to check if required to return reference variables in sub statements as well</param>
        /// <returns></returns>
        public static List<string> GetReferencedVariableNames(Statement s, bool onlyTopLevel)
        {
            var names = s.referencedVariables.Select(refVar => refVar.Name).ToList();
            if (!onlyTopLevel)
            {
                foreach (Statement subStatement in s.subStatements)
                    names.AddRange(GetReferencedVariableNames(subStatement, onlyTopLevel));
            }
            return names;
        }

        /// <summary>
        ///     Returns the names of the variables that have been declared in the statement
        /// </summary>
        /// <param name="s"> Statement whose variable names to be got.</param>
        /// <param name="onlyTopLevel"> Bool to check if required to return reference variables in sub statements as well</param>
        /// <returns></returns>
        public static List<string> GetDefinedVariableNames(Statement s, bool onlyTopLevel)
        {
            var names = s.definedVariables.Select(refVar => refVar.Name).ToList();
            if (!onlyTopLevel)
            {
                foreach (Statement subStatement in s.subStatements)
                    names.AddRange(GetReferencedVariableNames(subStatement, onlyTopLevel));
            }
            return names;
        }

        public static StatementType GetStatementType(Node astNode)
        {
            if (astNode is FunctionDefinitionNode)
                return StatementType.FuncDeclaration;
            if (astNode is BinaryExpressionNode)
            {
                var currentNode = astNode as BinaryExpressionNode;
                if (currentNode.Optr != Operator.assign)
                    throw new ArgumentException();
                if (!(currentNode.LeftNode.Name.StartsWith("temp") && currentNode.LeftNode.Name.Length > 10))
                    return StatementType.Expression;
                if (currentNode.RightNode is IdentifierNode)
                    return StatementType.AssignmentVar;
                if (currentNode.RightNode is ExprListNode)
                    return StatementType.Collection;
                if (currentNode.RightNode is DoubleNode || currentNode.RightNode is IntNode)
                    return StatementType.Literal;
                if (currentNode.RightNode is StringNode)
                    return StatementType.Literal;
            }
            return StatementType.None;
        }

        public static IdentifierNode GetDefinedIdentifier(Node leftNode)
        {
            if (leftNode is IdentifierNode)
                return leftNode as IdentifierNode;
            else if (leftNode is IdentifierListNode)
                return GetDefinedIdentifier((leftNode as IdentifierListNode).LeftNode);
            else if (leftNode is FunctionCallNode)
                return null;
            else
                throw new ArgumentException("Left node type incorrect");
        }
        #endregion

        #region Properties

        public int StartLine { get; private set; }
        public int EndLine { get; private set; }

        public Variable FirstDefinedVariable
        {
            get { return definedVariables.FirstOrDefault(); }
        }

        public State CurrentState { get; private set; }
        public StatementType CurrentType { get; private set; }
        public Node AstNode { get; private set; }

        #endregion

        #region Private Methods

        private Statement(Node parsedNode)
        {
            StartLine = parsedNode.line;
            EndLine = parsedNode.endLine;
            CurrentType = GetStatementType(parsedNode);
            this.AstNode = parsedNode;

            if (parsedNode is BinaryExpressionNode)
            {
                //First get all the defined variables
                while (parsedNode is BinaryExpressionNode)
                {
                    IdentifierNode assignedVar = GetDefinedIdentifier((parsedNode as BinaryExpressionNode).LeftNode);
                    if (assignedVar != null)
                        definedVariables.Add(new Variable(assignedVar));
                    parsedNode = (parsedNode as BinaryExpressionNode).RightNode;
                }

                //Then get the referenced variables
                List<Variable> refVariableList = new List<Variable>();
                GetReferencedVariables(parsedNode, refVariableList);
                referencedVariables = refVariableList;
            }
            else if (parsedNode is FunctionDefinitionNode)
            {
                throw new NotImplementedException();
            }
            else
                throw new ArgumentException("Must be func def or assignment");

            Variable.SetCorrectColumn(referencedVariables, CurrentType, StartLine);
        }

        #endregion
    }


    public class Variable
    {
        public int Row { get; private set; }
        public int StartColumn { get; private set; }

        public int EndColumn
        {
            get { return StartColumn + Name.Length; }
        }

        public string Name { get; private set; }

        #region Private Methods

        private void MoveColumnBack(int line)
        {
            //Move the column of the variable back only if it is on the same line
            //as the fake variable
            if (Row == line)
                StartColumn -= 13;
        }

        #endregion

        #region Public Methods

        public Variable(IdentifierNode identNode)
        {
            if (identNode == null)
                throw new ArgumentNullException();
            Name = identNode.Value;
            if (identNode.ArrayDimensions != null)
                ; //  Implement!
            Row = identNode.line;
            StartColumn = identNode.col;
        }

        public Variable(string name, int line)
        {
            Name = name;
            Row = line;
        }

        public static void SetCorrectColumn(List<Variable> refVar, Statement.StatementType type, int line)
        {
            if (refVar == null)
                return;
            if (type != Statement.StatementType.Expression)
            {
                foreach (Variable singleVar in refVar)
                    singleVar.MoveColumnBack(line);
            }
        }

        #endregion
    }
}