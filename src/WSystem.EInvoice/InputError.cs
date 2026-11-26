using System;

namespace WSystem.EInvoice
{
    /// <summary>The invoice data is incomplete or invalid (the W System API answers these with HTTP 400).</summary>
    public class InputError : ArgumentException
    {
        public InputError(string message) : base(message) { }
    }
}
