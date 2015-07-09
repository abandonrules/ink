﻿using System.Collections.Generic;
using System.Linq;

namespace Inklewriter.Parsed
{
	public class Divert : Parsed.Object
	{
		public Parsed.Path target { get; protected set; }
        public Parsed.Object targetContent { get; protected set; }
        public List<Expression> arguments { get; protected set; }
		public Runtime.Divert runtimeDivert { get; protected set; }
        public bool isFunctionCall { get; set; }
        public bool isToGather { get; set; }

        public Divert (Parsed.Path target, List<Expression> arguments = null)
		{
			this.target = target;
            this.arguments = arguments;

            if (arguments != null) {
                AddContent (arguments.Cast<Parsed.Object> ().ToList ());
            }
		}

        public Divert (Parsed.Object targetContent)
        {
            this.targetContent = targetContent;
        }

		public override Runtime.Object GenerateRuntimeObject ()
		{
            runtimeDivert = new Runtime.Divert ();

            // Normally we resolve the target content during the
            // Resolve phase, since we expect all runtime objects to
            // be available in order to find the final runtime path for
            // the destination. However, we need to resolve the target
            // (albeit without the runtime target) early so that
            // we can get information about the arguments - whether
            // they're by reference - since it affects the code we 
            // generate here.
            ResolveTargetContent ();

            // Passing arguments to the knot
            if ( ResolveArguments() || isFunctionCall ) {

                var container = new Runtime.Container ();

                if (!isFunctionCall) {
                    container.AddContent (Runtime.ControlCommand.EvalStart());
                }

                List<FlowBase.Argument> targetArguments = null;
                if( targetContent )
                    targetArguments = (targetContent as FlowBase).arguments;

                for (var i = 0; i < arguments.Count; ++i) {
                    Expression argToPass = arguments [i];
                    FlowBase.Argument argExpected = null; 
                    if( targetArguments != null ) 
                        argExpected = targetArguments [i];

                    // Pass by reference: argument needs to be a variable reference
                    if (argExpected != null && argExpected.isByReference) {

                        var varRef = argToPass as VariableReference;
                        if (varRef == null) {
                            Error ("Expected variable name to pass by reference to 'ref " + argExpected.name + "' but saw " + argToPass.ToString ());
                            break;
                        }

                        var varPointer = new Runtime.LiteralVariablePointer (varRef.name);
                        container.AddContent (varPointer);
                    } 

                    // Normal value being passed: evaluate it as normal
                    else {
                        argToPass.GenerateIntoContainer (container);
                    }
                }
                    
                if (!isFunctionCall) {
                    container.AddContent (Runtime.ControlCommand.EvalEnd());
                }

                // If this divert is a function call, we push to the call stack
                // so we can return again
                if (isFunctionCall) {
                    container.AddContent (Runtime.ControlCommand.StackPush());
                }

                // Jump into the "function" (knot/stitch)
                container.AddContent (runtimeDivert);

                return container;
            } 

            // Simple divert
            else {
                return runtimeDivert;
            }			
		}


        // When the divert is to a target that's actually a variable name
        // rather than an explicit knot/stitch name, try interpretting it
        // as such by getting the variable name.
        public string PathAsVariableName()
        {
            return target.firstComponent;
        }
            

        void ResolveTargetContent()
        {
            if (isToGather) {
                return;
            }

            if (targetContent == null) {

                // Is target of this divert a variable name that will be de-referenced
                // at runtime? If so, there won't be any further reference resolution
                // we can do at this point.
                var variableTargetName = PathAsVariableName ();
                if (variableTargetName != null) {
                    var flowBaseScope = ClosestFlowBase ();
                    if (flowBaseScope.ResolveVariableWithName (variableTargetName, fromNode:this)) {
                        runtimeDivert.variableDivertName = variableTargetName;
                        return;
                    }
                }

                targetContent = target.ResolveFromContext (this);

                if (targetContent == null) {

                    Path alternativePath = null;
                    targetContent = target.ResolveFromContext (this, forceSearchAnywhere:true);
                    if (targetContent) {
                        alternativePath = targetContent.PathRelativeTo (this);
                    }

                    if (targetContent) {
                        Warning ("target not found: '" + target.ToString () + "'. Assuming you meant '"+alternativePath+"'");
                        target = alternativePath;
                    } else {
                        var isWarning = target.baseTargetLevel == FlowLevel.Knot;
                        Error ("target not found: '" + target.ToString () + "'", this, isWarning);
                    }

                    // Add name to the debugMetadata so that if we get a runtime error,
                    // we have a human readable name for the original source target,
                    // rather than a runtime path.
                    debugMetadata.sourceName = target.ToString ();
                }

            }
        }

        public override void ResolveReferences(Story context)
		{
            if (isToGather) {
                return;
            }

            if (targetContent) {
                runtimeDivert.targetPath = targetContent.runtimePath;
            }

            // Resolve children (the arguments)
            base.ResolveReferences (context);
		}

        // Returns true if arguments require code generation (as opposed to whether there's an error,
        // though that's related)
        bool ResolveArguments()
        {
            if (isToGather) 
                return false;

            if (targetContent == null)
                return false;

            // Argument passing: Check for errors in number of arguments
            var numArgs = 0;
            if (arguments != null && arguments.Count > 0)
                numArgs = arguments.Count;

            FlowBase targetFlow = targetContent as FlowBase;

            // No error, crikey!
            if (numArgs == 0 && (targetFlow == null || !targetFlow.hasParameters)) {
                return false;
            }

            if (targetFlow == null && numArgs > 0) {
                Error ("target needs to be a knot or stitch in order to pass arguments");
                return false;
            }

            if (targetFlow.arguments == null && numArgs > 0) {
                Error ("target (" + targetFlow.name + ") doesn't take parameters");
                return false;
            }

            var paramCount = targetFlow.arguments.Count;
            if (paramCount > 0 && this.parent is DivertTarget) {
                Error ("Can't store a link to a knot that takes parameters in a variable");
                return false;
            }

            if (paramCount != numArgs) {

                string butClause;
                if (numArgs == 0) {
                    butClause = "but there weren't any passed to it";
                } else if (numArgs < paramCount) {
                    butClause = "but only got " + numArgs;
                } else {
                    butClause = "but got " + numArgs;
                }
                Error ("to '" + targetFlow.name + "' requires " + paramCount + " arguments, "+butClause);
                return false;
            }
                
            if (targetFlow == null) {
                Error ("Can't call as a function or with arguments unless it's a knot or stitch");
                return false;
            }

            return true;
        }

        public override void Error (string message, Object source = null, bool isWarning = false)
        {
            // Could be getting an error from a nested Divert
            if (source != this && source) {
                base.Error (message, source);
                return;
            }

            if (isFunctionCall) {
                base.Error ("Function call " + message, source, isWarning);
            } else {
                base.Error ("Divert " + message, source, isWarning);
            }
        }

	}
}

