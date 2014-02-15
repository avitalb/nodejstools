﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using Newtonsoft.Json.Linq;

namespace Microsoft.NodejsTools.Debugger.Serialization {
    sealed class NodeSetValueVariable : INodeVariable {
        public NodeSetValueVariable(NodeStackFrame stackFrame, string name, JToken message) {
            Id = (int)message["body"]["newValue"]["handle"];
            StackFrame = stackFrame;
            Parent = null;
            Name = name;
            TypeName = (string)message["body"]["newValue"]["type"];
            Value = (string)message["body"]["newValue"]["value"];
            Class = (string)message["body"]["newValue"]["className"];
            Text = (string)message["body"]["newValue"]["text"];
            Attributes = NodePropertyAttributes.None;
            Type = NodePropertyType.Normal;
        }

        public int Id { get; private set; }
        public NodeEvaluationResult Parent { get; private set; }
        public NodeStackFrame StackFrame { get; private set; }
        public string Name { get; private set; }
        public string TypeName { get; private set; }
        public string Value { get; private set; }
        public string Class { get; private set; }
        public string Text { get; private set; }
        public NodePropertyAttributes Attributes { get; private set; }
        public NodePropertyType Type { get; private set; }
    }
}