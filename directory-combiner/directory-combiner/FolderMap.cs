using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace directory_combiner
{
    public class FolderMap
    {
        public string MapFrom { get; private set; }
        public string MapTo { get; private set; }
        public FolderMap(string mapFrom, string mapTo)
        {
            MapFrom = mapFrom;
            MapTo = mapTo;
        }
    }
}
