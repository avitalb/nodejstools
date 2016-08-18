using System.ComponentModel.Composition;
using Microsoft.VisualStudio.InteractiveWindow;

namespace Microsoft.NodejsTools.ReplNew
{
    [Export(typeof(IInteractiveEvaluatorProvider))]
    class NodejsInteractiveEvaluatorProvider : IInteractiveEvaluatorProvider
    {
        internal const string NodeReplId = "{E4AC36B7-EDC5-4AD2-B758-B5416D520705}";

        public IInteractiveEvaluator GetEvaluator(string replId)
        {
            if (replId == NodeReplId)
            {
                return new NodejsInteractiveEvaluator();
            }
            return null;
        }

    }
}
