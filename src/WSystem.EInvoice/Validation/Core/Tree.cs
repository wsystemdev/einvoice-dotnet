using System.Collections.Generic;
using System.Text;
using System.Xml;

namespace WSystem.EInvoice.Validation.Core
{
    /// <summary>A node of <see cref="Tree"/>. Id = document order.</summary>
    internal sealed class Node
    {
        public const int DOC = 0, ELEM = 1, ATTR = 2, TEXT = 3, COMMENT = 4, PI = 5;
        public Node? Parent;
        public readonly List<Node> Children = new List<Node>();
        public readonly List<Node> Attrs = new List<Node>();
        public int SibIndex;
        private string? _sv;
        public readonly Tree Tree; public readonly int Id; public readonly int Kind;
        public readonly string Ns, Local, QName, Value;

        public Node(Tree tree, int id, int kind, string ns = "", string local = "", string qname = "", string value = "")
        { Tree = tree; Id = id; Kind = kind; Ns = ns; Local = local; QName = qname; Value = value; }

        public string StringValue()
        {
            if (_sv != null) return _sv;
            if (Kind == ELEM || Kind == DOC)
            {
                var sb = new StringBuilder();
                Collect(this, sb);
                return _sv = sb.ToString();
            }
            return _sv = Value;
        }

        private static void Collect(Node n, StringBuilder sb)
        {
            foreach (var c in n.Children)
            {
                if (c.Kind == TEXT) sb.Append(c.Value);
                else if (c.Kind == ELEM) sb.Append(c.StringValue());
            }
        }
    }

    /// <summary>Immutable, indexed copy of an XML document for fast XPath evaluation (document order = Node.Id).</summary>
    internal sealed class Tree
    {
        public readonly Node Root;
        public readonly List<Node> All = new List<Node>();

        public Tree(XmlDocument dom)
        {
            Root = new Node(this, 0, Node.DOC);
            All.Add(Root);
            foreach (XmlNode c in dom.ChildNodes) Add(Root, c, null);
        }

        private void Add(Node parent, XmlNode? d, string? text)
        {
            Node? n;
            if (text != null) n = new Node(this, All.Count, Node.TEXT, value: text);
            else if (d is XmlElement e) n = new Node(this, All.Count, Node.ELEM, e.NamespaceURI ?? "", e.LocalName, e.Name);
            else if (d is XmlComment cm) n = new Node(this, All.Count, Node.COMMENT, value: cm.Data);
            else if (d is XmlProcessingInstruction pi) n = new Node(this, All.Count, Node.PI, local: pi.Target, qname: pi.Target, value: pi.Data);
            else if (d is XmlCharacterData cd && (d is XmlText || d is XmlCDataSection || d is XmlWhitespace || d is XmlSignificantWhitespace))
                n = parent.Kind == Node.DOC ? null : new Node(this, All.Count, Node.TEXT, value: cd.Data);
            else n = null;
            if (n == null) return;
            if (n.Kind == Node.TEXT && parent.Children.Count > 0 && parent.Children[parent.Children.Count - 1].Kind == Node.TEXT) return;
            n.Parent = parent;
            n.SibIndex = parent.Children.Count;
            parent.Children.Add(n);
            All.Add(n);
            if (d is XmlElement el)
            {
                foreach (XmlAttribute a in el.Attributes)
                {
                    if (a.NamespaceURI == "http://www.w3.org/2000/xmlns/") continue; // namespace declarations are not attributes
                    var an = new Node(this, All.Count, Node.ATTR, a.NamespaceURI ?? "", a.LocalName, a.Name, a.Value) { Parent = n };
                    n.Attrs.Add(an);
                    All.Add(an);
                }
                StringBuilder? buf = null;
                foreach (XmlNode c in el.ChildNodes)
                {
                    if (c is XmlText || c is XmlCDataSection || c is XmlWhitespace || c is XmlSignificantWhitespace)
                    {
                        (buf ??= new StringBuilder()).Append(((XmlCharacterData)c).Data);
                        continue;
                    }
                    if (buf != null) { Add(n, null, buf.ToString()); buf = null; }
                    Add(n, c, null);
                }
                if (buf != null) Add(n, null, buf.ToString());
            }
        }
    }
}
