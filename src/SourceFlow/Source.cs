using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public class Source
    {
        public int Id { get; set; }

        public Source(int id, Type type)
        {
            Id = id;
            Type = type;
        }

        public Type Type { get; set; }
    }
}