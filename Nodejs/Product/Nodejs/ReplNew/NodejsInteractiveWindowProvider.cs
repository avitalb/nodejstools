using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.InteractiveWindow.Shell;
using Microsoft.VisualStudio.InteractiveWindow;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Imaging;

namespace Microsoft.NodejsTools.ReplNew
{
    [Export(typeof(NodejsInteractiveWindowProvider))]
    class NodejsInteractiveWindowProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IInteractiveEvaluator _evaluator;
        private readonly IVsInteractiveWindowFactory _windowFactory;
        //private readonly IContentType _nodeContentType;
        private readonly IContentType _typescriptContentType;
        private IVsInteractiveWindow _replWindow;

        [ImportingConstructor]
        public NodejsInteractiveWindowProvider(
            [Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider,
            [Import] IVsInteractiveWindowFactory factory,
            [Import] IInteractiveEvaluatorProvider evaluatorProvider,
            [Import] IContentTypeRegistryService contentTypeService
        )
        {
            _serviceProvider = serviceProvider;
            _evaluator = evaluatorProvider.GetEvaluator("{E4AC36B7-EDC5-4AD2-B758-B5416D520705}");
            _windowFactory = factory;
            //_nodeContentType = contentTypeService.GetContentType(NodejsConstants.Nodejs);
            _typescriptContentType = contentTypeService.GetContentType(NodejsConstants.JavaScript);
        }

        public IVsInteractiveWindow OpenOrCreate(string replId)
        {
            if (_replWindow == null)
            {
                _replWindow = Create(replId);
            }
            _replWindow.Show(true);
            return _replWindow;
        }

        private IVsInteractiveWindow CreateInteractiveWindowInternal(
            IInteractiveEvaluator evaluator,
            IContentType contentType,
            bool alwaysCreate,
            string title,
            Guid languageServiceGuid,
            string replId
        )
        {
            var creationFlags = __VSCREATETOOLWIN.CTW_fActivateWithProject;
            if (alwaysCreate)
            {
                creationFlags |= __VSCREATETOOLWIN.CTW_fForceCreate;
            }

            //var window = _windowFactory.Create(Guids.NodejsLanguageInfo, 0, title, evaluator, creationFlags);
            var window = _windowFactory.Create(Guids.TypeScriptLanguageInfo, 0, title, evaluator, creationFlags);
            var toolWindow = _replWindow as ToolWindowPane;
            if (toolWindow != null)
            {
                toolWindow.BitmapImageMoniker = KnownMonikers.PYInteractiveWindow;
            }
            //replace with Node.u
            window.SetLanguage(Guids.TypeScriptLanguageInfo, contentType);
            window.InteractiveWindow.InitializeAsync();

            return window;
        }
        public IVsInteractiveWindow Create(string replId)
        {
            var window = CreateInteractiveWindowInternal(
                _evaluator,
                _typescriptContentType,
                true,
                "Node.js Interactive Window",
                Guids.TypeScriptLanguageInfo,
                replId
            );
            return window;
        }
    }
}
