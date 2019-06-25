using System.Collections.Generic;

namespace QtSharp.DocGeneration
{
    public class FunctionDocIndexNode : FullNameDocIndexNode
    {
        public FunctionDocIndexNode()
        {
            this.ParametersTypes = new List<string>();
        }

        public string Access { get; set; }
        public List<string> ParametersTypes { get; private set; }
    }
}
