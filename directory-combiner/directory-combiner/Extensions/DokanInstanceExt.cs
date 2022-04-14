using DokanNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace directory_combiner.Extensions
{
    public class DokanInstanceExt : DokanInstance
    {
        public DokanInstanceExt() : base()
        {

        }

        public bool IsDisposed {get;private set;}

        protected override void Dispose(bool disposing) 
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
